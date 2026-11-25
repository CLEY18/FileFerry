using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// In-memory implementation of <see cref="ILocalFileSystem"/> for engine tests.
/// Behavior is pinned by the ContractTests suite; this class must pass the same suite as
/// the plain FS and SAF. Directories: explicit (<see cref="CreateFolderAsync"/>) plus
/// implicit on write; time comes only from <see cref="IClock"/>.
/// </summary>
public sealed class InMemoryFileSystem : ILocalFileSystem
{
    private sealed record Node(byte[] Content, DateTimeOffset ModifiedAt);

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<string, Node> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { string.Empty };

    /// <summary>
    /// Create the fake FS; defaults to a deterministic <see cref="MutableClock"/> that
    /// tests turn explicitly.
    /// </summary>
    public InMemoryFileSystem(IClock? clock = null) => _clock = clock ?? new MutableClock();

    /// <inheritdoc cref="ILocalFileSystem.SnapshotAsync"/>
    public Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // same cancellation contract as the real adapters: a stop is an exception, not a failure
        lock (_gate)
        {
            var snapshots = new List<FileSnapshot>(_files.Count + _folders.Count);
            foreach (var folder in _folders.Where(f => f.Length > 0))
            {
                snapshots.Add(new FileSnapshot(RelativePath.Parse(folder), EntryKind.Directory, 0));
            }

            foreach (var (path, node) in _files)
            {
                snapshots.Add(new FileSnapshot(
                    RelativePath.Parse(path), EntryKind.File, node.Content.Length, node.ModifiedAt));
            }

            snapshots.Sort(static (a, b) => a.Path.CompareTo(b.Path));
            return Task.FromResult(Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(snapshots));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.ExistsAsync"/>
    public Task<Result<bool, SyncError>> ExistsAsync(RelativePath path, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = path.ToString();
            return Task.FromResult(Result<bool, SyncError>.Success(
                _files.ContainsKey(key) || _folders.Contains(key)));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.ReadAsync"/>
    public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct)
    {
        lock (_gate)
        {
            return _files.TryGetValue(path.ToString(), out var node)
                ? Task.FromResult(Result<Stream, SyncError>.Success(new MemoryStream(node.Content, writable: false)))
                : Task.FromResult(Result<Stream, SyncError>.Failure(NotFound(path)));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.WriteAsync"/>
    public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // same cancellation contract as the real adapters
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var modifiedAt = _clock.UtcNow;

        lock (_gate)
        {
            var key = path.ToString();
            if (_folders.Contains(key))
            {
                return Task.FromResult(Result<FileSnapshot, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"'{path}' is a directory, not a file.")));
            }

            EnsureParents(path);
            _files[key] = new Node(bytes, modifiedAt);
            return Task.FromResult(Result<FileSnapshot, SyncError>.Success(
                new FileSnapshot(path, EntryKind.File, bytes.Length, modifiedAt)));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.CreateFolderAsync"/>
    public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = path.ToString();
            if (_files.ContainsKey(key))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"'{path}' is a file, not a directory.")));
            }

            EnsureParents(path);
            _folders.Add(key);
            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.CopyAsync"/>
    public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); // same cancellation contract as the real adapters
        lock (_gate)
        {
            if (!_files.TryGetValue(from.ToString(), out var node))
            {
                return Task.FromResult(Result<FileSnapshot, SyncError>.Failure(NotFound(from)));
            }

            var key = to.ToString();
            if (_folders.Contains(key))
            {
                return Task.FromResult(Result<FileSnapshot, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"Target '{to}' is a directory.")));
            }

            EnsureParents(to);
            var copy = node with { ModifiedAt = _clock.UtcNow }; // the copy moment — like a real provider
            _files[key] = copy;
            return Task.FromResult(Result<FileSnapshot, SyncError>.Success(
                new FileSnapshot(to, EntryKind.File, copy.Content.LongLength, copy.ModifiedAt)));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.MoveAsync"/>
    public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        lock (_gate)
        {
            var fromKey = from.ToString();
            var toKey = to.ToString();
            var isFile = _files.ContainsKey(fromKey);
            var isFolder = !isFile && _folders.Contains(fromKey);
            if (!isFile && !isFolder)
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(NotFound(from)));
            }

            if (_files.ContainsKey(toKey) || _folders.Contains(toKey))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"Target '{to}' already exists.")));
            }

            EnsureParents(to);
            if (isFile)
            {
                var node = _files[fromKey];
                _files.Remove(fromKey);
                _files[toKey] = node;
            }
            else
            {
                MoveSubtree(fromKey, toKey);
            }

            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.DeleteAsync"/>
    public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = path.ToString();
            if (_files.Remove(key))
            {
                return Task.FromResult(Result<Ok, SyncError>.Success());
            }

            if (path.IsEmpty || !_folders.Remove(key))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(NotFound(path)));
            }

            RemoveSubtree(key);
            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
    }

    /// <summary>
    /// Create implicit parent directories of a path (otherwise a write "into the air"
    /// would be unfair).
    /// </summary>
    private void EnsureParents(RelativePath path)
    {
        for (var parent = path.Parent; !parent.IsEmpty; parent = parent.Parent)
        {
            _folders.Add(parent.ToString());
        }
    }

    /// <summary>
    /// Move a directory subtree by rewriting key prefixes.
    /// </summary>
    private void MoveSubtree(string fromKey, string toKey)
    {
        var prefix = fromKey + "/";
        var movedFolders = _folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        var movedFiles = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

        foreach (var folder in movedFolders)
        {
            _folders.Remove(folder);
            _folders.Add(toKey + folder[fromKey.Length..]);
        }

        foreach (var file in movedFiles)
        {
            var node = _files[file];
            _files.Remove(file);
            _files[toKey + file[fromKey.Length..]] = node;
        }

        _folders.Add(toKey);
    }

    /// <summary>
    /// Delete everything below a directory (files and directories alike).
    /// </summary>
    private void RemoveSubtree(string key)
    {
        var prefix = key + "/";
        foreach (var file in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _files.Remove(file);
        }

        foreach (var folder in _folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _folders.Remove(folder);
        }
    }

    /// <summary>
    /// Canonical "not found" failure shared by all methods.
    /// </summary>
    private static SyncError NotFound(RelativePath path) =>
        SyncError.Of(ErrorKind.NotFound, $"Entry '{(path.IsEmpty ? "." : path)}' not found.");
}
