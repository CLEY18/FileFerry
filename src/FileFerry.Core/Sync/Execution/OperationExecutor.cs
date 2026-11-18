using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync.Execution;

/// <summary>
/// Execution summary: applied operations (in order) — input for BaselineUpdater (UoW) 
/// plus the failure that stopped the run (the baseline then doesn't move).
/// </summary>
/// <param name="Applied">Operations carried to Applied state.</param>
/// <param name="FailedOperation">The operation at fault for the failure; <see langword="null"/> on success.</param>
/// <param name="Error">Expected failure; <see langword="null"/> on success.</param>
public sealed record ExecutionReport(IReadOnlyList<SyncOperation> Applied, SyncOperation? FailedOperation, SyncError? Error)
{
    /// <summary>
    /// The run reached the end without failures.
    /// </summary>
    public bool Completed => Error is null;
}

/// <summary>
/// The pipeline's only place with IO. Every execution goes through the pair's
/// write-ahead journal: the whole plan is pinned <em>before</em> the first side effect, each step
/// goes Planned → Applied/Failed. A run cut short by cancellation or process death leaves the
/// journal behind; <see cref="ResumeAsync"/> replays only the unfinished steps — idempotent
/// handlers guarantee "no duplicates". A step failure instead quarantines the journal: the plan
/// must not replay its own contradiction against the sides.
/// Guard no deletion without a copy in the stash.
/// </summary>
public sealed class OperationExecutor
{
    private readonly ILocalFileSystem _local;
    private readonly IWritableRemoteStore _remote;
    private readonly ISyncStateRepository _state;
    private readonly IStashPolicy _stash;
    private readonly IReadOnlyDictionary<OperationKind, IOperationHandler> _handlers;
    private readonly Action<Guid>? _onStepApplied;

    /// <param name="local">Local side — handed to handlers via <see cref="ExecutionSides"/>.</param>
    /// <param name="remote">Remote side — handed to handlers via <see cref="ExecutionSides"/>.</param>
    /// <param name="state">State repository (operation journal).</param>
    /// <param name="stash">Stash policy — the root for the invariant guard.</param>
    /// <param name="handlers">Handler registry (OCP); defaults — the built-in set.</param>
    /// <param name="onStepApplied">Progress tick per applied step; <see langword="null"/> — no events.</param>
    public OperationExecutor(
        ILocalFileSystem local,
        IWritableRemoteStore remote,
        ISyncStateRepository state,
        IStashPolicy stash,
        IEnumerable<IOperationHandler>? handlers = null,
        Action<Guid>? onStepApplied = null)
    {
        _local = local;
        _remote = remote;
        _state = state;
        _stash = stash;
        _onStepApplied = onStepApplied;
        _handlers = (handlers ?? DefaultHandlers())
            .GroupBy(static h => h.Kind)
            .ToDictionary(static g => g.Key, static g => g.First());
    }

    /// <summary>
    /// Built-in registry: one idempotent handler per <see cref="OperationKind"/>.
    /// </summary>
    public static IReadOnlyList<IOperationHandler> DefaultHandlers() =>
    [
        new CreateRemoteFolderHandler(),
        new CreateLocalFolderHandler(),
        new UploadToRemoteHandler(),
        new DownloadToLocalHandler(),
        new MoveRemoteHandler(),
        new MoveLocalHandler(),
        new CopyLocalHandler(),
        new DeleteRemoteHandler(),
        new DeleteLocalHandler(),
        new RecordConvergenceHandler(),
    ];

    /// <summary>
    /// Fresh run: an empty journal is the entry condition (otherwise it conflicts with an
    /// interrupted run — invariant "one pair, one active run"). The plan is written into
    /// the journal upfront.
    /// </summary>
    /// <param name="context">The run context — whose pair the fresh journal is keyed.</param>
    /// <param name="plan">The planner's topologically ordered plan.</param>
    /// <param name="ct">Cancellation (fires between steps; the journal stays for resume).</param>
    public async Task<ExecutionReport> ExecuteAsync(
        SyncRunContext context, IReadOnlyList<SyncOperation> plan, CancellationToken ct)
    {
        var existing = await _state.LoadJournalAsync(context.Pair.Id, ct).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return new ExecutionReport([], null, SyncError.Of(
                ErrorKind.Conflict,
                $"The pair has an unfinished journal ({existing.Count} entries) — <c>ResumeAsync</c> is required.",
                cause: null));
        }

        var entries = new List<JournalEntry>(plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            var entry = new JournalEntry(i + 1, plan[i], JournalState.Planned, context.Now);
            entries.Add(entry);
            await _state.AppendJournalAsync(context.Pair.Id, entry, ct).ConfigureAwait(false);
        }

        return await RunJournalAsync(context, entries, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resume an interrupted run: replays only Planned/Failed steps in Seq order. Handler
    /// idempotence makes the replay safe — no duplicates appear on the sides.
    /// </summary>
    /// <param name="context">Run context (must be the same pair).</param>
    /// <param name="ct">Cancellation (fires between steps; the journal stays for the next resume).</param>
    public async Task<ExecutionReport> ResumeAsync(SyncRunContext context, CancellationToken ct)
    {
        var journal = await _state.LoadJournalAsync(context.Pair.Id, ct).ConfigureAwait(false);
        return await RunJournalAsync(context, journal, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sequential journal pass: skip Applied, execute the rest, stash guard before delete.
    /// </summary>
    private async Task<ExecutionReport> RunJournalAsync(
        SyncRunContext context, IReadOnlyList<JournalEntry> entries, CancellationToken ct)
    {
        var sides = new ExecutionSides(_local, _remote, context.Pair, RemotePath.Parse(context.Pair.RemoteRootPath));
        var applied = new List<SyncOperation>();
        var stashSources = new HashSet<RelativePath>();
        var observed = new Dictionary<RelativePath, FileSnapshot>(); // real per-path evidence as the journal rewrote the sides

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.State == JournalState.Applied)
            {
                applied.Add(entry.Operation);
                TrackStashSource(entry.Operation, stashSources);
                _onStepApplied?.Invoke(context.Pair.Id); // resume: already-applied steps move the progress bar too
                continue;
            }

            // The guard is about content: a victim directory deliberately has no copy of its
            // own (the planner doesn't copy directories); the content is covered by the children's
            // copies, replayed earlier in the journal (deeper first) — each under the same guard.
            if (entry.Operation.IsDelete && entry.Operation.After?.IsDirectory != true
                && !stashSources.Contains(entry.Operation.Path))
            {
                await UpdateAsync(context.Pair.Id, entry, JournalState.Failed, ct).ConfigureAwait(false);
                await QuarantineAsync(context.Pair.Id, ct).ConfigureAwait(false);
                return new ExecutionReport(applied, entry.Operation, SyncError.Of(
                    ErrorKind.Conflict,
                    $"Delete of '{entry.Operation.Path}' without a stash copy — the plan breaks the delete guard.",
                    cause: null));
            }

            var result = await ExecuteSafelyAsync(entry.Operation, sides, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                await UpdateAsync(context.Pair.Id, entry, JournalState.Failed, ct).ConfigureAwait(false);
                await QuarantineAsync(context.Pair.Id, ct).ConfigureAwait(false);
                return new ExecutionReport(applied, entry.Operation, result.Error);
            }

            // Evidence from the port (fresh ETag/record snapshot) becomes part of the operation — into both the journal and the baseline.
            var evidence = result.Value;
            if (evidence is { ModifiedAt: { } echoed } && entry.Operation.After?.ModifiedAt == echoed
                && observed.TryGetValue(entry.Operation.Path, out var rewritten)
                && rewritten.ModifiedAt is { } real && real != echoed)
            {
                // The handler echoed the planned timestamp, but a step of this same journal already
                // rewrote the path: the earlier observation is the moment the side really carries —
                // a planned time the file never had would make the next scan see a phantom change.
                evidence = evidence with { ModifiedAt = real };
            }

            var stamped = entry.Operation with { After = evidence ?? entry.Operation.After };
            await UpdateAsync(context.Pair.Id, entry with { Operation = stamped }, JournalState.Applied, ct)
                .ConfigureAwait(false);

            applied.Add(stamped);
            TrackStashSource(stamped, stashSources);
            TrackObserved(stamped, evidence, observed);
            _onStepApplied?.Invoke(context.Pair.Id); // tick only for a step actually applied
        }

        await _state.ClearJournalAsync(context.Pair.Id, ct).ConfigureAwait(false);
        return new ExecutionReport(applied, null, null);
    }

    /// <summary>
    /// Journal quarantine: a failed step means the plan contradicts the sides, and replaying it
    /// would loop the same conflict forever ("poisoned resume"). What was applied already lies
    /// <see cref="ExecutionReport.Applied"/> — SyncRun projects it into the baseline, and
    /// the rest is deterministically re-planned next run from (local, remote, baseline).
    /// Cancellation never gets here (OperationCanceledException unwinds above) — a journal cut
    /// short by cancellation or process death stays intact for an idempotent resume.
    /// </summary>
    private Task QuarantineAsync(Guid pairId, CancellationToken ct) => _state.ClearJournalAsync(pairId, ct);

    /// <summary>
    /// Handler invocation converting an unexpected exception into an expected <see cref="SyncError"/>.
    /// </summary>
    private async Task<Result<FileSnapshot?, SyncError>> ExecuteSafelyAsync(
        SyncOperation op, ExecutionSides sides, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(op.Kind, out var handler))
        {
            return Result<FileSnapshot?, SyncError>.Failure(
                SyncError.Of(ErrorKind.Conflict, $"No handler for operation '{op.Kind}' — the registry is incomplete."));
        }

        try
        {
            return await handler.ExecuteAsync(op, sides, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<FileSnapshot?, SyncError>.Failure(
                SyncError.Of(ErrorKind.Unknown, $"Unexpected failure of '{op.Kind}' on '{op.Path}'.", ex));
        }
    }

    /// <summary>
    /// Entry state update (upsert under the same Seq; the operation already carries its evidence stamp).
    /// </summary>
    private Task UpdateAsync(Guid pairId, JournalEntry entry, JournalState state, CancellationToken ct) =>
        _state.AppendJournalAsync(pairId, entry with { State = state }, ct);

    /// <summary>
    /// A copy into the stash root makes the source's later deletion legitimate.
    /// </summary>
    /// <summary>
    /// The real evidence the journal holds per path: writes and copies record the side's actual
    /// moment, a move carries it to the target, a delete forgets the path — so a later step on the
    /// same path is never judged against a time the side never carried.
    /// </summary>
    private static void TrackObserved(
        SyncOperation op, FileSnapshot? evidence, Dictionary<RelativePath, FileSnapshot> observed)
    {
        switch (op.Kind)
        {
            case OperationKind.DeleteLocal or OperationKind.DeleteRemote:
                observed.Remove(op.Path);
                break;

            case OperationKind.MoveLocal or OperationKind.MoveRemote when op.SourcePath is { } source:
                observed.Remove(source);
                if (op.After is { } moved)
                {
                    observed[op.Path] = evidence ?? moved;
                }

                break;

            default:
                if (evidence is not null)
                {
                    observed[op.Path] = evidence;
                }

                break;
        }
    }

    private void TrackStashSource(SyncOperation op, HashSet<RelativePath> stashSources)
    {
        if (op.SourcePath is { } source
            && op.Kind is OperationKind.CopyLocal or OperationKind.DownloadToLocal
            && op.Path.StartsWith(_stash.StashRoot))
        {
            stashSources.Add(source);
        }
    }
}
