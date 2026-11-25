using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Data;

/// <summary>
/// Plain-FS implementation of <see cref="ILocalFileSystem"/> over an ordinary directory
/// (dev machine, integration tests; Android uses <c>SafLocalFileSystem</c>). Writes are
/// tmp + rename (atomicity). Behavior is pinned by the <c>LocalFileSystemContract</c> suite.
/// </summary>
public sealed class PlainLocalFileSystem : ILocalFileSystem
{
    private readonly string _root;

    /// <summary>
    /// Create the adapter over a pair-root directory, creating it when missing.
    /// </summary>
    public PlainLocalFileSystem(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc cref="ILocalFileSystem.SnapshotAsync"/>
    public async Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct)
    {
        try
        {
            var snapshots = new List<FileSnapshot>();
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                var relative = ToRelative(directory);
                if (!relative.IsEmpty)
                {
                    snapshots.Add(new FileSnapshot(relative, EntryKind.Directory, 0));
                }
            }

            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                snapshots.Add(new FileSnapshot(
                    ToRelative(file), EntryKind.File, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }

            snapshots.Sort(static (a, b) => a.Path.CompareTo(b.Path));
            return Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(snapshots);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancellation is not an I/O outcome — it travels on as cancellation, stack included
        }
        catch (Exception ex)
        {
            return FromException<IReadOnlyList<FileSnapshot>>(ex, _root);
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.ExistsAsync"/>
    public Task<Result<bool, SyncError>> ExistsAsync(RelativePath path, CancellationToken ct)
    {
        var full = ToFull(path);
        try
        {
            return Task.FromResult(Result<bool, SyncError>.Success(
                System.IO.File.Exists(full) || Directory.Exists(full)));
        }
        catch (Exception e)
        {
            return Task.FromResult(Result<bool, SyncError>.Failure(
                SyncError.Of(ErrorKind.PermissionLost, $"Cannot probe '{path}': {e.Message}")));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.ReadAsync"/>
    public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct)
    {
        var full = ToFull(path);
        try
        {
            if (Directory.Exists(full))
            {
                return Task.FromResult(Result<Stream, SyncError>.Failure(NotFound(path)));
            }

            if (!File.Exists(full))
            {
                return Task.FromResult(Result<Stream, SyncError>.Failure(NotFound(path)));
            }

            Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            return Task.FromResult(Result<Stream, SyncError>.Success(stream));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FromException<Stream>(ex, full));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.WriteAsync"/>
    public async Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct)
    {
        var full = ToFull(path);
        var tmp = Path.Combine(
            Path.GetDirectoryName(full)!,
            LocalTempNames.ForPlainFile(Path.GetFileName(full)));
        try
        {
            if (Directory.Exists(full))
            {
                return Result<FileSnapshot, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"'{path}' is a directory, not a file."));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using (var target = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            File.Move(tmp, full, overwrite: true); // atomic rename over the target (POSIX rename; SAF emulates it on Android)
            var info = new FileInfo(full);
            return Result<FileSnapshot, SyncError>.Success(new FileSnapshot(
                path, EntryKind.File, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw; // the stop button pressed: the caller must see cancellation, not a failure to retry
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return FromException<FileSnapshot>(ex, full);
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.CreateFolderAsync"/>
    public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct)
    {
        var full = ToFull(path);
        try
        {
            if (File.Exists(full))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"'{path}' is a file, not a directory.")));
            }

            Directory.CreateDirectory(full); // idempotent
            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FromException<Ok>(ex, full));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.CopyAsync"/>
    public async Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        var fullFrom = ToFull(from);
        var fullTo = ToFull(to);
        try
        {
            if (!File.Exists(fullFrom))
            {
                return Result<FileSnapshot, SyncError>.Failure(NotFound(from));
            }

            if (Directory.Exists(fullTo))
            {
                return Result<FileSnapshot, SyncError>.Failure(
                    SyncError.Of(ErrorKind.Conflict, $"Target '{to}' is a directory."));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullTo)!);
            await using (var source = new FileStream(fullFrom, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(fullTo, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            // evidence as written: the file system stamped the copy's own mtime, the scan will show exactly it
            var info = new FileInfo(fullTo);
            return Result<FileSnapshot, SyncError>.Success(new FileSnapshot(
                to, EntryKind.File, info.Length, info.LastWriteTimeUtc));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // same rule as the write: a stopped run is reported by the exception, never as a failure
        }
        catch (Exception ex)
        {
            return FromException<FileSnapshot>(ex, fullTo);
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.MoveAsync"/>
    public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct)
    {
        var fullFrom = ToFull(from);
        var fullTo = ToFull(to);
        try
        {
            if (File.Exists(fullFrom))
            {
                if (File.Exists(fullTo) || Directory.Exists(fullTo))
                {
                    return Task.FromResult(Result<Ok, SyncError>.Failure(Occupied(to)));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullTo)!);
                File.Move(fullFrom, fullTo);
                return Task.FromResult(Result<Ok, SyncError>.Success());
            }

            if (Directory.Exists(fullFrom))
            {
                if (File.Exists(fullTo) || Directory.Exists(fullTo))
                {
                    return Task.FromResult(Result<Ok, SyncError>.Failure(Occupied(to)));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullTo)!);
                Directory.Move(fullFrom, fullTo);
                return Task.FromResult(Result<Ok, SyncError>.Success());
            }

            return Task.FromResult(Result<Ok, SyncError>.Failure(NotFound(from)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FromException<Ok>(ex, fullTo));
        }
    }

    /// <inheritdoc cref="ILocalFileSystem.DeleteAsync"/>
    public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct)
    {
        var full = ToFull(path);
        try
        {
            if (File.Exists(full))
            {
                File.Delete(full);
                return Task.FromResult(Result<Ok, SyncError>.Success());
            }

            if (path.IsEmpty || !Directory.Exists(full))
            {
                return Task.FromResult(Result<Ok, SyncError>.Failure(NotFound(path)));
            }

            Directory.Delete(full, recursive: true);
            return Task.FromResult(Result<Ok, SyncError>.Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FromException<Ok>(ex, full));
        }
    }

    /// <summary>
    /// Absolute path with a guarantee that the relative one cannot escape the root
    /// (double defense after the value object).
    /// </summary>
    private string ToFull(RelativePath relative)
    {
        var candidate = Path.GetFullPath(Path.Combine(_root, relative.ToString()));
        if (!candidate.StartsWith(_root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && candidate != _root)
        {
            throw new UnauthorizedAccessException($"The path '{relative}' escapes the pair root.");
        }

        return candidate;
    }

    /// <summary>
    /// Relative <see cref="RelativePath"/> built from an absolute path.
    /// </summary>
    private RelativePath ToRelative(string absolute) =>
        RelativePath.Parse(Path.GetRelativePath(_root, absolute).Replace(Path.DirectorySeparatorChar, '/'));

    /// <summary>
    /// Canonical "not found" failure.
    /// </summary>
    private static SyncError NotFound(RelativePath path) =>
        SyncError.Of(ErrorKind.NotFound, $"Entry '{(path.IsEmpty ? "." : path)}' not found.");

    /// <summary>
    /// Canonical "target occupied" failure.
    /// </summary>
    private static SyncError Occupied(RelativePath path) =>
        SyncError.Of(ErrorKind.Conflict, $"The target '{path}' already exists.");

    /// <summary>
    /// Maps FS exceptions onto <see cref="ErrorKind"/> categories (expected outcomes go
    /// through Result, never exceptions). Cancellation never enters here as a category: the
    /// callers above rethrow it while the token is the run's — it must not come back dressed
    /// as a retryable failure.
    /// </summary>
    private static Result<TValue, SyncError> FromException<TValue>(Exception ex, string path) => ex switch
    {
        UnauthorizedAccessException => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.PermissionLost, $"No access to '{path}'.", ex)),
        IOException io when io.HResult == unchecked((int)0x80070070) => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.StorageFull, $"Storage is full: '{path}'.", io)),
        IOException io => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.LocalIo, $"I/O error for '{path}'.", io)),
        _ => Result<TValue, SyncError>.Failure(SyncError.Of(ErrorKind.Unknown, $"Unexpected FS error for '{path}'.", ex)),
    };

    /// <summary>
    /// Best effort to clean up the tmp file after a failed write.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // the tmp file stays behind — better than masking the failure's root cause
        }
    }
}
