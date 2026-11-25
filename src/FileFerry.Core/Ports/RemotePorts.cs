using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Ports;

/// <summary>
/// Reading the remote tree (PROPFIND Depth:1 + GET). A separate ISP role: the UI's folder-picker needs only this.
/// </summary>
public interface IRemoteBrowser
{
    /// <summary>
    /// One directory level (Depth 1) — for UI navigation and scanning with ETag-pruning.
    /// </summary>
    /// <param name="folder">Directory from the user's DAV root.</param>
    /// <param name="ct">Run cancellation (all the way down to HTTP).</param>
    Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct);

    /// <summary>
    /// Open a file for reading (GET). The caller closes the returned stream. Content streams —
    /// a large file is never held whole in memory; the stream may sit on the live connection,
    /// so it is read to the end (or dropped) within the caller's operation, never seeked back.
    /// </summary>
    /// <param name="file">File from the user's DAV root.</param>
    /// <param name="ct">Cancels the request (all the way down to HTTP).</param>
    Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct);
}

/// <summary>
/// Creating/writing remote objects (PUT / MKCOL). Writing looks atomic to the engine: either a full file or a failure.
/// </summary>
public interface IRemoteWriter
{
    /// <summary>
    /// Write a file (large ones — chunked v2 inside the adapter); returns a fresh <see cref="RemoteEntry"/> with the new ETag.
    /// The destination collection must already exist (folders are the plan's own phase) — a write
    /// into a missing parent fails with the server's refusal and creates nothing.
    /// </summary>
    /// <param name="file">Destination from the user's DAV root.</param>
    /// <param name="content">Content stream (the adapter does not close the input stream).</param>
    /// <param name="ct">Cancels the upload; the engine sees failure, never a partial file.</param>
    Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct);

    /// <summary>
    /// Create a collection (MKCOL), intermediates included — idempotent: already exists ≡ success.
    /// </summary>
    Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct);
}

/// <summary>
/// Rename/move on the server (MOVE) — one request, not delete+create.
/// </summary>
public interface IRemoteMover
{
    /// <summary>
    /// Move an entry; target already exists → <see cref="ErrorKind.Conflict"/>.
    /// </summary>
    Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct);
}

/// <summary>
/// Delete from the server (DELETE). Called only after a copy in the stash — invariant is held by the planner.
/// </summary>
public interface IRemoteDeleter
{
    /// <summary>
    /// Delete an entry recursively (file or collection).
    /// </summary>
    Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct);
}

/// <summary>
/// Composite port for the executor (I): read+write+move+delete in a single adapter object.
/// </summary>
public interface IWritableRemoteStore : IRemoteBrowser, IRemoteWriter, IRemoteMover, IRemoteDeleter
{
}
