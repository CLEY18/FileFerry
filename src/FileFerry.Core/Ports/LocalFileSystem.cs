using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Ports;

/// <summary>
/// The key port: the local FS as a normalized snapshot model over <see cref="RelativePath"/>.
/// Implementations — SAF (Android), plain-FS (PC/integration), in-memory (tests); behavior is pinned
/// by the contract suite in ContractTests — the fake substitutes for the real one (L).
/// </summary>
public interface ILocalFileSystem
{
    /// <summary>
    /// Full snapshot of the pair's local root (tree walk; SAF — batch ContentResolver queries).
    /// Returns both files and directories; order is arbitrary (the engine sorts deterministically).
    /// </summary>
    Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Open a file for reading. The caller closes the stream.
    /// </summary>
    Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct);

    /// <summary>
    /// Atomic write: tmp file + rename; returns a snapshot of what was written
    /// (with hash/size).
    /// </summary>
    /// <param name="path">Target file; an existing one is atomically replaced.</param>
    /// <param name="content">Input stream (not closed by the adapter).</param>
    /// <param name="ct">Cancels the write; a cancelled write leaves the previous file intact.</param>
    Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct);

    /// <summary>
    /// Create a directory including intermediates; already exists → success (idempotent).
    /// </summary>
    Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct);

    /// <summary>
    /// Copy a file inside the tree (for the stash: the source stays untouched).
    /// </summary>
    Task<Result<Ok, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct);

    /// <summary>
    /// Rename/move (not copy+delete): preserves the entry's identity.
    /// </summary>
    Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct);

    /// <summary>
    /// Delete an entry (the planner guarantees "stash first" — that is not re-checked here).
    /// </summary>
    Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct);
}
