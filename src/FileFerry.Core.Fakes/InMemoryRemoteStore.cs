using System.Security.Cryptography;
using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// In-memory implementation of <see cref="IWritableRemoteStore"/> — the "server" for
/// planner and executor tests. Emulates Nextcloud head semantics: a fresh ETag on every
/// PUT, MOVE as a single entry, idempotent MKCOL.
/// </summary>
public sealed class InMemoryRemoteStore : IWritableRemoteStore
{
    private sealed record Node(byte[] Content, string? ETag, DateTimeOffset ModifiedAt);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<string, Node> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { string.Empty };
    private int _etagCounter;

    /// <summary>
    /// Create the fake "server"; defaults to deterministic time for mtimes.
    /// </summary>
    public InMemoryRemoteStore(IClock? clock = null) => _clock = clock ?? new MutableClock();

    /// <inheritdoc cref="IRemoteBrowser.ListAsync"/>
    public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = folder.ToString();
            if (!_folders.Contains(key))
            {
                return Task.FromResult(Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(
                    SyncError.Of(ErrorKind.NotFound, $"The collection '{folder}' was not found.")));
            }

            var prefix = folder.IsRoot ? string.Empty : key + "/";
            var children = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
            CollectChildren(children, _files.Select(static kv => (Key: kv.Key, Kind: EntryKind.File, Size: kv.Value.Content.LongLength, ETag: kv.Value.ETag, ModifiedAt: (DateTimeOffset?)kv.Value.ModifiedAt)), prefix);
            CollectChildren(children, _folders.Where(static f => f.Length > 0).Select(static k => (Key: k, Kind: EntryKind.Directory, Size: 0L, ETag: (string?)null, ModifiedAt: (DateTimeOffset?)null)), prefix);

            var list = children.Values
                .OrderBy(static e => e.Path, RemotePathComparer.Instance)
                .Select(e => e.Kind == EntryKind.Directory ? e with { ETag = DirectoryETag(e.Path) } : e)
                .ToList(); // like real NC: collections carry an ETag, stable while the subtree is unchanged
            return Task.FromResult(Result<IReadOnlyList<RemoteEntry>, SyncError>.Success(list));
        }
    }

    /// <inheritdoc cref="IRemoteBrowser.ReadAsync"/>
    public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct)
    {
        lock (_gate)
        {
            return _files.TryGetValue(file.ToString(), out var node)
                ? Task.FromResult(Result<Stream, SyncError>.Success(new MemoryStream(node.Content, writable: false)))
                : Task.FromResult(Result<Stream, SyncError>.Failure(
                    SyncError.Of(ErrorKind.NotFound, $"The file '{file}' was not found.")));
        }
    }

    /// <inheritdoc cref="IRemoteWriter.WriteAsync"/>
    public Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        lock (_gate)
        {
            var key = file.ToString();
            if (_folders.Contains(key))
            {
                return Task.FromResult(Result<RemoteEntry, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"'{file}' is a collection.")));
            }

            if (!HasLiveParent(file))
            {
                return Task.FromResult(Result<RemoteEntry, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"The collection of '{file}' does not exist — MKCOL first.")));
            }

            var entry = new Node(buffer.ToArray(), NextETag(), _clock.UtcNow);
            _files[key] = entry;
            return Task.FromResult(Result<RemoteEntry, SyncError>.Success(EntryOf(file, entry)));
        }
    }

    /// <summary>
    /// Fixture pre-population: store a file together with its ancestor collections — what a bare
    /// MKCOL chain does on a real server before the first test write. Not the port's contract.
    /// </summary>
    public Task<Result<RemoteEntry, SyncError>> SeedAsync(RemotePath file, Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        lock (_gate)
        {
            EnsureParents(file);
            var entry = new Node(buffer.ToArray(), NextETag(), _clock.UtcNow);
            _files[file.ToString()] = entry;
            return Task.FromResult(Result<RemoteEntry, SyncError>.Success(EntryOf(file, entry)));
        }
    }

    /// <inheritdoc cref="IRemoteWriter.CreateFolderAsync"/>
    public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = folder.ToString();
            if (_files.ContainsKey(key))
            {
                return Task.FromResult(Result<RemoteEntry, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"'{folder}' is a file; MKCOL is impossible.")));
            }

            EnsureParents(folder);
            _folders.Add(key); // idempotent: already there → no-op
            return Task.FromResult(Result<RemoteEntry, SyncError>.Success(new RemoteEntry(folder, EntryKind.Directory, 0)));
        }
    }

    /// <inheritdoc cref="IRemoteMover.MoveAsync"/>
    public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct)
    {
        lock (_gate)
        {
            var fromKey = from.ToString();
            var toKey = to.ToString();
            var isFile = _files.ContainsKey(fromKey);
            if (!isFile && !_folders.Contains(fromKey))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(
                    SyncError.Of(ErrorKind.NotFound, $"'{from}' was not found.")));
            }

            if (_files.ContainsKey(toKey) || _folders.Contains(toKey))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"The target '{to}' already exists (NC would answer 412).")));
            }

            EnsureParents(to);
            var prefix = fromKey + "/";
            if (isFile)
            {
                var node = _files[fromKey];
                _files.Remove(fromKey);
                _files[toKey] = node;
            }
            else
            {
                foreach (var k in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    _files[toKey + k[fromKey.Length..]] = _files[k];
                    _files.Remove(k);
                }

                foreach (var f in _folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    _folders.Remove(f);
                    _folders.Add(toKey + f[fromKey.Length..]);
                }

                _folders.Remove(fromKey); // a MOVE relocates the collection — the source side goes empty
                _folders.Add(toKey);
            }

            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
    }

    /// <inheritdoc cref="IRemoteDeleter.DeleteAsync"/>
    public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = path.ToString();
            if (_files.Remove(key))
            {
                return Task.FromResult(Result<Ok, SyncError>.Success());
            }

            if (path.IsRoot || !_folders.Remove(key))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(
                    SyncError.Of(ErrorKind.NotFound, $"'{path}' was not found.")));
            }

            var prefix = key + "/";
            foreach (var k in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                _files.Remove(k);
            }

            foreach (var f in _folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                _folders.Remove(f);
            }

            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
    }

    /// <summary>
    /// Children one level deep: direct children plus "virtual" directories for
    /// intermediate levels. Paths are built from already-decoded keys —
    /// <see cref="RemotePath.ParseDecoded"/>, no second decode.
    /// </summary>
    private static void CollectChildren(
        Dictionary<string, RemoteEntry> acc,
        IEnumerable<(string Key, EntryKind Kind, long Size, string? ETag, DateTimeOffset? ModifiedAt)> keys,
        string prefix)
    {
        foreach (var (key, keyKind, size, etag, modifiedAt) in keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length)
            {
                continue;
            }

            var rest = key[prefix.Length..];
            var slash = rest.IndexOf('/');
            var name = slash < 0 ? rest : rest[..slash];
            var kind = slash < 0 ? keyKind : EntryKind.Directory;

            // a deeper descendant ≡ a directory; a same-level file stays a file with its real
            // size, mtime and ETag — like a live PROPFIND (without file-level evidence,
            // strong-evidence merge could not work)
            if (!acc.TryGetValue(name, out var existing) || existing.Kind == EntryKind.File && kind == EntryKind.Directory)
            {
                acc[name] = new RemoteEntry(
                    RemotePath.ParseDecoded(prefix + name),
                    kind,
                    kind == EntryKind.File ? size : 0,
                    ModifiedAt: kind == EntryKind.File ? modifiedAt : existing?.ModifiedAt,
                    ETag: kind == EntryKind.File ? etag : null);
            }
        }
    }

    /// <summary>
    /// Collection ETag: a deterministic hash over (path, ETag) of every descendant file.
    /// Any descendant change changes it — exactly the server property ETag-pruning rests on.
    /// </summary>
    private string DirectoryETag(RemotePath folder)
    {
        var prefix = folder.IsRoot ? string.Empty : folder + "/";
        var subtree = string.Join('|', _files
            .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(static f => f.Key, StringComparer.Ordinal)
            .Select(static f => f.Key + "=" + f.Value.ETag));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(subtree));
        return $"\"{Convert.ToHexStringLower(digest[..8])}\"";
    }

    /// <summary>
    /// Fixture pre-registration of an existing collection — the wizard creates the pair root on
    /// the server before the first run, so the fake starts with it. Ancestors are registered too.
    /// </summary>
    public void SeedCollection(RemotePath folder)
    {
        lock (_gate)
        {
            EnsureParents(folder);
            _folders.Add(folder.ToString());
        }
    }

    /// <summary>
    /// Create parent collections for MKCOL/MOVE targets (the plan creates folders top-down, so
    /// in a real run the parent already exists; the fake tolerates a directly created subtree).
    /// </summary>
    private void EnsureParents(RemotePath path)
    {
        for (var parent = path.Parent; !parent.IsRoot; parent = parent.Parent)
        {
            _folders.Add(parent.ToString());
        }
    }

    /// <summary>
    /// Whether every collection above the path exists: a PUT into a missing collection is the
    /// server's 409 — the fake must refuse it too, folders are the plan's own phase.
    /// </summary>
    private bool HasLiveParent(RemotePath path)
    {
        for (var parent = path.Parent; !parent.IsRoot; parent = parent.Parent)
        {
            if (!_folders.Contains(parent.ToString()))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Next "server" ETag — unique per write.
    /// </summary>
    private string NextETag() => $"\"{Interlocked.Increment(ref _etagCounter):x16}\"";

    /// <summary>
    /// Assemble the <see cref="RemoteEntry"/> view of a stored node.
    /// </summary>
    private static RemoteEntry EntryOf(RemotePath path, Node node) =>
        new(path, EntryKind.File, node.Content.Length, node.ModifiedAt, node.ETag);
}

/// <summary>
/// Ordering comparer for stable ListAsync output (depth first, then ordinal).
/// </summary>
internal sealed class RemotePathComparer : Comparer<RemotePath>
{
    public static readonly RemotePathComparer Instance = new();

    /// <inheritdoc />
    public override int Compare(RemotePath? x, RemotePath? y) => x?.CompareTo(y) ?? (y is null ? 0 : -1);
}
