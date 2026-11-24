using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync.Execution;

/// <summary>
/// Operation handlers (OCP: a new kind — a new class in the registry). Each is
/// <b>idempotent</b>: re-running an applied operation (resume) returns success without
/// duplicate side effects. Success carries <b>evidence of the applied state</b> (a
/// <see cref="FileSnapshot"/> for the baseline) — or <see langword="null"/> when there is nothing
/// beyond the planned snapshot (the executor keeps the planned one).
/// </summary>
internal static class HandlerResult
{
    /// <summary>
    /// Success without new evidence — the baseline keeps the planned <c>After</c>.
    /// </summary>
    internal static Result<FileSnapshot?, SyncError> Evidenceless(Result<Ok, SyncError> result) =>
        result.IsSuccess
            ? Result<FileSnapshot?, SyncError>.Success(null)
            : Result<FileSnapshot?, SyncError>.Failure(result.Error);
}

/// <summary>
/// MKCOL on the server; the adapter is idempotent per the ports contract. The collection's ETag
/// is healed by the scan backfill.
/// </summary>
public sealed class CreateRemoteFolderHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.CreateRemoteFolder;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        var created = await sides.Remote.CreateFolderAsync(sides.RemoteOf(op.Path), ct).ConfigureAwait(false);
        return created.IsSuccess
            ? Result<FileSnapshot?, SyncError>.Success(null) // the collection's ETag gets backfilled by the scan anyway
            : Result<FileSnapshot?, SyncError>.Failure(created.Error);
    }
}

/// <summary>
/// MKDIR locally; idempotent per the ports contract.
/// </summary>
public sealed class CreateLocalFolderHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.CreateLocalFolder;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct) =>
        HandlerResult.Evidenceless(await sides.Local.CreateFolderAsync(op.Path, ct).ConfigureAwait(false));
}

/// <summary>
/// Local file → server (PUT); a repeated PUT overwrites — no duplicates appear.
/// Evidence: the planned local snapshot (coordinate side — local mtime) ⊕ the fresh ETag 
/// the PUT response.
/// </summary>
public sealed class UploadToRemoteHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.UploadToRemote;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        var read = await sides.Local.ReadAsync(op.SourcePath ?? op.Path, ct).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return Result<FileSnapshot?, SyncError>.Failure(read.Error);
        }

        await using (read.Value.ConfigureAwait(false))
        {
            var written = await sides.Remote.WriteAsync(sides.RemoteOf(op.Path), read.Value, ct).ConfigureAwait(false);
            if (written.IsFailure)
            {
                return Result<FileSnapshot?, SyncError>.Failure(written.Error);
            }

            var after = op.After is { } planned
                ? planned with { Size = written.Value.Size, ETag = written.Value.ETag }
                : new FileSnapshot(op.Path, EntryKind.File, written.Value.Size, written.Value.ModifiedAt, written.Value.ETag);
            return Result<FileSnapshot?, SyncError>.Success(after);
        }
    }
}

/// <summary>
/// Server file → local (GET + tmp/rename); SourcePath is the server-side source when it differs.
/// Evidence: the local write snapshot (local mtime/size) + the ETag from the planned remote snapshot.
/// </summary>
public sealed class DownloadToLocalHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.DownloadToLocal;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        var read = await sides.Remote.ReadAsync(sides.RemoteOf(op.SourcePath ?? op.Path), ct).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return Result<FileSnapshot?, SyncError>.Failure(read.Error);
        }

        await using (read.Value.ConfigureAwait(false))
        {
            var written = await sides.Local.WriteAsync(op.Path, read.Value, ct).ConfigureAwait(false);
            if (written.IsFailure)
            {
                return Result<FileSnapshot?, SyncError>.Failure(written.Error);
            }

            return Result<FileSnapshot?, SyncError>.Success(
                written.Value with { ETag = written.Value.ETag ?? op.After?.ETag });
        }
    }
}

/// <summary>
/// MOVE on the server. Idempotence: source gone, target present → the operation was already applied.
/// </summary>
public sealed class MoveRemoteHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.MoveRemote;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        if (op.SourcePath is null)
        {
            return Result<FileSnapshot?, SyncError>.Failure(
                SyncError.Of(ErrorKind.Conflict, $"{nameof(OperationKind.MoveRemote)} without a source."));
        }

        var to = sides.RemoteOf(op.Path);
        var moved = await sides.Remote.MoveAsync(sides.RemoteOf(op.SourcePath), to, ct).ConfigureAwait(false);
        if (moved.IsSuccess || moved.Error.Kind != ErrorKind.NotFound)
        {
            return HandlerResult.Evidenceless(moved);
        }

        return await RemoteProbe.ExistsAsync(sides.Remote, to, ct).ConfigureAwait(false)
            ? Result<FileSnapshot?, SyncError>.Success(null)
            : HandlerResult.Evidenceless(moved);
    }
}

/// <summary>
/// Rename locally. Idempotence: source gone, target present → already applied.
/// </summary>
public sealed class MoveLocalHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.MoveLocal;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        if (op.SourcePath is null)
        {
            return Result<FileSnapshot?, SyncError>.Failure(
                SyncError.Of(ErrorKind.Conflict, $"{nameof(OperationKind.MoveLocal)} without a source."));
        }

        var moved = await sides.Local.MoveAsync(op.SourcePath, op.Path, ct).ConfigureAwait(false);
        if (moved.IsSuccess || moved.Error.Kind != ErrorKind.NotFound)
        {
            return HandlerResult.Evidenceless(moved);
        }

        // One open answers "was the move already applied?" — a full tree snapshot would cost more
        // than the move it checks. The planner emits this move only for files, so opening is the
        // right probe; an unreadable target stays an honest failure.
        var probe = await sides.Local.ReadAsync(op.Path, ct).ConfigureAwait(false);
        if (probe.IsSuccess)
        {
            probe.Value.Dispose();
            return Result<FileSnapshot?, SyncError>.Success(null);
        }

        return HandlerResult.Evidenceless(moved);
    }
}

/// <summary>
/// Copy inside the local tree (stash/conflict copies); overwriting the target is idempotent.
/// </summary>
public sealed class CopyLocalHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.CopyLocal;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        if (op.SourcePath is null)
        {
            return Result<FileSnapshot?, SyncError>.Failure(
                SyncError.Of(ErrorKind.Conflict, $"{nameof(OperationKind.CopyLocal)} without a source."));
        }

        // Evidence: the port's snapshot of the fresh copy — its mtime is the copy moment; the
        // planned source snapshot is a time the copy never carries (the next run would churn).
        var copied = await sides.Local.CopyAsync(op.SourcePath, op.Path, ct).ConfigureAwait(false);
        return copied.IsSuccess
            ? Result<FileSnapshot?, SyncError>.Success(copied.Value)
            : Result<FileSnapshot?, SyncError>.Failure(copied.Error);
    }
}

/// <summary>
/// DELETE from the server. Idempotence: AlreadyGone ≡ applied (interrupted run).
/// </summary>
public sealed class DeleteRemoteHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.DeleteRemote;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        var deleted = await sides.Remote.DeleteAsync(sides.RemoteOf(op.Path), ct).ConfigureAwait(false);
        return deleted.IsSuccess || deleted.Error.Kind == ErrorKind.NotFound
            ? Result<FileSnapshot?, SyncError>.Success(null)
            : HandlerResult.Evidenceless(deleted);
    }
}

/// <summary>
/// DELETE locally. Idempotence: AlreadyGone ≡ applied (interrupted run).
/// </summary>
public sealed class DeleteLocalHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.DeleteLocal;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public async Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        var deleted = await sides.Local.DeleteAsync(op.Path, ct).ConfigureAwait(false);
        return deleted.IsSuccess || deleted.Error.Kind == ErrorKind.NotFound
            ? Result<FileSnapshot?, SyncError>.Success(null)
            : HandlerResult.Evidenceless(deleted);
    }
}

/// <summary>
/// Convergence — no touch of the sides at all. Sharedness was already proven by DiffEngine with
/// strong evidence (hash/etag); "applying" only means the evidence passes through the write-ahead
/// journal and stamps the applied operation — from there BaselineUpdater legally seeds the row.
/// Idempotent by construction: a re-run only changes the journal.
/// </summary>
public sealed class RecordConvergenceHandler : IOperationHandler
{
    /// <inheritdoc cref="IOperationHandler.Kind"/>
    public OperationKind Kind => OperationKind.RecordConvergence;

    /// <inheritdoc cref="IOperationHandler.ExecuteAsync"/>
    public Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Result<FileSnapshot?, SyncError>.Success(op.After));
    }
}

/// <summary>
/// Lazy existence check of a remote record via a Depth-1 listing of its parent (failure path only).
/// </summary>
internal static class RemoteProbe
{
    /// <summary>
    /// Whether the record exists on the server (parent listing + path match).
    /// </summary>
    /// <param name="remote">The remote store whose parent listing is queried.</param>
    /// <param name="path">Path from the DAV-root.</param>
    /// <param name="ct">Cancellation (this is a failure-path probe only).</param>
    internal static async Task<bool> ExistsAsync(Ports.IWritableRemoteStore remote, RemotePath path, CancellationToken ct)
    {
        var listing = await remote.ListAsync(path.Parent, ct).ConfigureAwait(false);
        return listing.IsSuccess && listing.Value.Any(e => e.Path.Equals(path));
    }
}
