using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync.Execution;

/// <summary>
/// The sides available to one operation's handlers: local FS, remote store, and the mapping 
/// pair coordinates (<see cref="RelativePath"/> → <see cref="RemotePath"/>).
/// </summary>
/// <param name="Local">Local side (the executor's port, pair-local coordinates).</param>
/// <param name="Remote">Remote side (the executor's composed port).</param>
/// <param name="Pair">The folder pair — with <c>Root</c> it defines the coordinate mapping.</param>
/// <param name="Root">The pair's root on the server (from the user's DAV-root).</param>
public sealed record ExecutionSides(ILocalFileSystem Local, IWritableRemoteStore Remote, FolderPair Pair, RemotePath Root)
{
    /// <summary>
    /// Pair coordinate → server path.
    /// </summary>
    public RemotePath RemoteOf(RelativePath path) => Root.Combine(path);
}

/// <summary>
/// Handler for a single <see cref="OperationKind"/> (Strategy + registry, OCP): a new
/// operation kind means a new class without editing the executor. The contract is
/// <b>idempotence</b>: re-running an already-applied operation (resume after interruption)
/// must succeed without duplicates.
/// </summary>
public interface IOperationHandler
{
    /// <summary>
    /// The operation kind this handler is registered for — the executor's dispatch key.
    /// </summary>
    OperationKind Kind { get; }

    /// <summary>
    /// Execute the operation against the sides; expected failures are Results, not exceptions.
    /// The success value is evidence of the applied state for the baseline, or null — the planned
    /// snapshot stays.
    /// </summary>
    /// <param name="op">The planned operation (already in pair coordinates).</param>
    /// <param name="sides">Both sides plus the pair's coordinate mapping.</param>
    /// <param name="ct">Run cancellation; a cancelled step must stay replayable (idempotence).</param>
    Task<Result<FileSnapshot?, SyncError>> ExecuteAsync(SyncOperation op, ExecutionSides sides, CancellationToken ct);
}
