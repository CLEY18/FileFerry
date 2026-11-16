using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync.Execution;

namespace FileFerry.Core.Sync;

/// <summary>
/// Summary of one run for the caller (Worker/UI): what happened, how many operations, why it stopped.
/// </summary>
/// <param name="RunId">Run identifier (log-table key).</param>
/// <param name="Outcome">How the run ended: Success, Partial, Failed or Cancelled.</param>
/// <param name="Planned">How many operations were in the plan/journal.</param>
/// <param name="Applied">How many were carried to Applied.</param>
/// <param name="Conflicts">How many conflict annotations the planner produced.</param>
/// <param name="Error">The expected failure that stopped the run; <see langword="null"/> on success.</param>
public sealed record SyncRunReport(
    Guid RunId,
    SyncRunOutcome Outcome,
    int Planned,
    int Applied,
    int Conflicts,
    SyncError? Error);

/// <summary>
/// Orchestrator of pipeline for one pair: probe → scan → plan → execute → baseline-UoW → run-log.
/// Invariants: keyed lock (one pair — one active run; a second trigger is refused without a
/// queue); the baseline moves only by actually applied operations in one UoW (even when the
/// run is stopped by a step failure); a cancelled/crashed journal takes priority over planning
/// (resume), and a journal with a step failure goes to quarantine — the next run plans from state.
/// Cancellation stops the run between steps; the journal stays.
/// </summary>
public sealed class SyncRun
{
    private readonly ISyncScopeFactory _scopes;
    private readonly SyncPlanner _planner;
    private readonly ISyncStateRepository _state;
    private readonly BaselineUpdater _baseline;
    private readonly IClock _clock;
    private readonly IStashPolicy _stash;
    private readonly IEnumerable<IOperationHandler>? _handlers;
    private readonly ISyncRunObserver? _observer;
    private readonly IHashProvider? _hasher;
    private readonly IStashRepository? _stashIndex;
    private readonly PairLockRegistry _locks = new();

    /// <summary>
    /// Assemble the orchestrator from pipeline dependencies (a DI singleton for the app).
    /// </summary>
    /// <param name="scopes">Pair-scope factory (adapters live outside — Core stays pure).</param>
    /// <param name="planner">Pure facade: scan results → plan.</param>
    /// <param name="state">State repository: journal, baseline, run logs.</param>
    /// <param name="stash">Stash policy — the executor's guard and scan filtering.</param>
    /// <param name="clock">Time source (determinism, invariant).</param>
    /// <param name="handlers">Executor handler registry; <see langword="null"/> — the built-in set.</param>
    /// <param name="observer">Run progress for Worker→UI; <see langword="null"/> — no events.</param>
    /// <param name="hasher">Hash evidence for the weak-collision hatch; <see langword="null"/> — the planner
    /// conservatively conflicts everything not proven by ETag.</param>
    /// <param name="stashIndex">Copy-buffer index for the TTL-purge; <see langword="null"/> disables the
    /// purge (tests only; production DI must pass it — otherwise the trash-buffer promise isn't kept).</param>
    public SyncRun(
        ISyncScopeFactory scopes,
        SyncPlanner planner,
        ISyncStateRepository state,
        IStashPolicy stash,
        IClock clock,
        IEnumerable<IOperationHandler>? handlers = null,
        ISyncRunObserver? observer = null,
        IHashProvider? hasher = null,
        IStashRepository? stashIndex = null)
    {
        _scopes = scopes;
        _planner = planner;
        _state = state;
        _stash = stash;
        _clock = clock;
        _handlers = handlers;
        _observer = observer;
        _hasher = hasher;
        _stashIndex = stashIndex;
        _baseline = new BaselineUpdater(state, stash);
    }

    /// <summary>
    /// One full pair run. No free lock → immediate <see cref="ErrorKind.Conflict"/> refusal:
    /// a repeat trigger during an active run is redundant (WorkManager unique-REPLACE does the same).
    /// </summary>
    /// <param name="pairId">The pair to run — also the key of the one-active-run lock.</param>
    /// <param name="ct">Run cancellation (the journal stays for resume).</param>
    /// <param name="ignorePaused">"Run now" from the UI: ignores the pair's paused flag.</param>
    public async Task<Result<SyncRunReport, SyncError>> RunAsync(
        Guid pairId, CancellationToken ct, bool ignorePaused = false)
    {
        var gate = _locks.TryAcquire(pairId, ct);
        if (gate is null)
        {
            return Result<SyncRunReport, SyncError>.Failure(SyncError.Of(
                ErrorKind.Conflict,
                "The pair already has an active run — one pair, one run — so the trigger was ignored."));
        }

        using (gate)
        {
            return await RunGuardedAsync(pairId, ct, ignorePaused).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run body under the lock: timing, events, cancellation, final run-log.
    /// </summary>
    private async Task<Result<SyncRunReport, SyncError>> RunGuardedAsync(
        Guid pairId, CancellationToken ct, bool ignorePaused)
    {
        var runId = Guid.NewGuid();
        var startedAt = _clock.UtcNow;
        var events = new List<SyncEvent> { MakeEvent(EventKind.RunStarted) };

        try
        {
            return await RunPipelineAsync(runId, pairId, startedAt, events, ct, ignorePaused)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a working situation (the Worker stopped the run): unfinished steps are already in the journal.
            events.Add(MakeEvent(EventKind.RunCancelled, SyncEventOutcome.Warning));
            await SaveRunAsync(runId, pairId, startedAt, SyncRunOutcome.Cancelled, events).ConfigureAwait(false);
            return Result<SyncRunReport, SyncError>.Success(
                new SyncRunReport(runId, SyncRunOutcome.Cancelled, 0, 0, 0, null));
        }
    }

    /// <summary>
    /// The pipeline sequentially: every stage failure is an expected Result + a run-log with the event.
    /// </summary>
    private async Task<Result<SyncRunReport, SyncError>> RunPipelineAsync(
        Guid runId, Guid pairId, DateTimeOffset startedAt, List<SyncEvent> events,
        CancellationToken ct, bool ignorePaused)
    {
        var scopeResult = await _scopes.OpenAsync(pairId, ct).ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return await FailAsync(runId, pairId, startedAt, scopeResult.Error,
                EventPhrases.FailScope, events).ConfigureAwait(false);
        }

        var scope = scopeResult.Value;
        if (scope.Pair.Paused && !ignorePaused)
        {
            return Result<SyncRunReport, SyncError>.Failure(SyncError.Of(
                ErrorKind.Conflict, $"The pair '{scope.Pair.DisplayName}' is paused — the run was not started."));
        }

        // Gatekeeper status.php — a cheaper signal than a scan storm of requests to a dead server.
        var status = await scope.Probe.ProbeAsync(ct).ConfigureAwait(false);
        if (status.IsFailure)
        {
            return await FailAsync(runId, pairId, startedAt, status.Error,
                EventPhrases.FailProbe, events).ConfigureAwait(false);
        }

        if (status.Value.MaintenanceMode)
        {
            // diagnostic only — the EventFailMaintenance phrase carries the localized label
            var maintenance = SyncError.Of(ErrorKind.ServerGone, "status.php reported maintenance=true.");
            return await FailAsync(runId, pairId, startedAt, maintenance,
                EventPhrases.FailMaintenance, events).ConfigureAwait(false);
        }

        var context = new SyncRunContext(
            scope.Pair, _clock.UtcNow,
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>());
        var executor = new OperationExecutor(
            scope.Local, scope.Remote, _state, _stash, _handlers,
            _observer is null ? null : pid => _observer.OnStepApplied(pid));

        var journal = await _state.LoadJournalAsync(pairId, ct).ConfigureAwait(false);
        int planned;
        int conflicts;
        IReadOnlyList<FileSnapshot>? remoteScanned = null;
        IReadOnlyList<FileSnapshot>? localScanned = null;
        ExecutionReport report;

        if (journal.Count > 0)
        {
            // an interrupted run has priority — finish the journal first, plan afterwards.
            planned = journal.Count;
            conflicts = 0;
            _observer?.OnPlanned(pairId, planned); // resume: the full count is known before the first tick
            events.Add(MakeEvent(
                EventKind.ResumeStopped, SyncEventOutcome.Warning, count: journal.Count));

            // Resume also scans remote (without a local scan or planning) — for the ETag backfill
            // the baseline: JournalEntry.After + the fresh scan seed the complete baseline.
            var resumeScan = await RemoteScanner
                .ScanAsync(scope.Remote, RemotePath.Parse(scope.Pair.RemoteRootPath),
                    await _state.LoadBaselineAsync(pairId, ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
            remoteScanned = resumeScan.IsSuccess ? resumeScan.Value : null; // a scan failure doesn't block the resume

            report = await executor.ResumeAsync(context, ct).ConfigureAwait(false);
        }
        else
        {
            var baseline = await _state.LoadBaselineAsync(pairId, ct).ConfigureAwait(false);
            var localScan = await scope.Local.SnapshotAsync(ct).ConfigureAwait(false);
            if (localScan.IsFailure)
            {
                return await FailAsync(runId, pairId, startedAt, localScan.Error,
                    EventPhrases.FailLocalScan, events).ConfigureAwait(false);
            }

            // The baseline loaded earlier: its collection ETags feed subtree pruning.
            var remoteScan = await RemoteScanner
                .ScanAsync(scope.Remote, RemotePath.Parse(scope.Pair.RemoteRootPath), baseline, ct)
                .ConfigureAwait(false);
            if (remoteScan.IsFailure)
            {
                return await FailAsync(runId, pairId, startedAt, remoteScan.Error,
                    EventPhrases.FailRemoteScan, events).ConfigureAwait(false);
            }

            var localEvidence = localScan.Value;
            var remoteEvidence = remoteScan.Value;
            if (_hasher is { } hasher)
            {
                // Before planning, backfill hash evidence wherever "the same?" hangs on bare size.
                (localEvidence, remoteEvidence) = await EvidenceHatch.HatchAsync(
                    scope.Local, scope.Remote, hasher, localEvidence, remoteEvidence, baseline,
                    RemotePath.Parse(scope.Pair.RemoteRootPath), ct).ConfigureAwait(false);
            }

            remoteScanned = remoteEvidence;
            localScanned = localEvidence;

            var plan = _planner.Plan(
                scope.Pair,
                Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(localEvidence),
                Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(remoteEvidence),
                baseline,
                _clock.UtcNow);
            if (plan.IsFailure)
            {
                return await FailAsync(runId, pairId, startedAt, plan.Error,
                    EventPhrases.FailPlanner, events).ConfigureAwait(false);
            }

            planned = plan.Value.Operations.Count;
            conflicts = plan.Value.Conflicts.Count;

            _observer?.OnPlanned(pairId, planned); // "N of M": the total is known before the first applied step
            foreach (var conflict in plan.Value.Conflicts)
            {
                events.Add(MakeEvent(
                    EventKind.ConflictDetected, SyncEventOutcome.Warning, conflict.Path,
                    conflict: conflict.Kind.ToString()));
            }

            events.Add(MakeEvent(EventKind.PlanSummary, count: planned));
            report = await executor.ExecuteAsync(context, plan.Value.Operations, ct).ConfigureAwait(false);
        }

        // UoW the whole set of applied operations is projected into the baseline by one
        // replacement; fresh scan evidence heals rows the operations didn't touch.
        // This also happens on a step failure (journal quarantined): "applied" ≠ "run
        // completed", and without this the applied state would become a "ghost" and churn the next plan.
        // Cancellation never gets here (the OCE branch above) — its journal is replayed idempotently by resume.
        await _baseline
            .ApplyAsync(pairId, report.Applied, remoteScanned, localScanned, ct)
            .ConfigureAwait(false);

        // TTL-purge of the buffer — after the applied set and the baseline, at the end of the run; best-effort:
        // a provider failure leaves the row for the next pass, cancellation stops only the purge itself.
        if (_stashIndex is { } stashIndex)
        {
            var purged = await StashPurger
                .PurgeExpiredAsync(pairId, scope.Local, stashIndex, _stash, _clock, ct)
                .ConfigureAwait(false);
            if (purged > 0)
            {
                events.Add(MakeEvent(EventKind.StashPurged, count: purged));
            }
        }

        var outcome = report.Completed
            ? SyncRunOutcome.Success
            : report.Applied.Count > 0 ? SyncRunOutcome.Partial : SyncRunOutcome.Failed;

        events.Add(report.Completed
            ? MakeEvent(EventKind.AppliedBaseline, SyncEventOutcome.Success, count: report.Applied.Count)
            : MakeEvent(EventKind.RunStopped, SyncEventOutcome.Error, detail: report.Error?.Message));

        await SaveRunAsync(runId, pairId, startedAt, outcome, events).ConfigureAwait(false);
        return Result<SyncRunReport, SyncError>.Success(
            new SyncRunReport(runId, outcome, planned, report.Applied.Count, conflicts, report.Error));
    }

    /// <summary>
    /// A stage failed: Error event, Failed run-log, the failure returned with the same error.
    /// </summary>
    private async Task<Result<SyncRunReport, SyncError>> FailAsync(
        Guid runId, Guid pairId, DateTimeOffset startedAt, SyncError error,
        string label, List<SyncEvent> events)
    {
        events.Add(MakeEvent(EventKind.RunFailed, SyncEventOutcome.Error, label: label, detail: error.Message));
        await SaveRunAsync(runId, pairId, startedAt, SyncRunOutcome.Failed, events).ConfigureAwait(false);
        return Result<SyncRunReport, SyncError>.Failure(error);
    }

    /// <summary>
    /// Final run-log write. Written without <c>ct</c>: the log must survive the run's
    /// cancellation, otherwise the "Journal" screen wouldn't show "Cancelled". A write that the
    /// storage rejects degrades here: the run-log is presentation data, so neither a lost row nor
    /// a throwing repository may replace the stage error or the report the caller acts on —
    /// an escaping exception would also reach the scheduler as a crash to retry.
    /// </summary>
    private async Task SaveRunAsync(
        Guid runId, Guid pairId, DateTimeOffset startedAt, SyncRunOutcome outcome, List<SyncEvent> events)
    {
        try
        {
            await _state.SaveRunLogAsync(
                new SyncRunLog(runId, pairId, startedAt, _clock.UtcNow, outcome, events.ToArray()),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the log may be lost; the outcome may not
        }
    }

    /// <summary>
    /// Journal event timed by <see cref="IClock"/>: only the kind + phrase parameters;
    /// the App renders them via AppStrings, so the journal repaints in the UI language.
    /// </summary>
    private SyncEvent MakeEvent(
        EventKind kind,
        SyncEventOutcome outcome = SyncEventOutcome.Info,
        RelativePath? path = null,
        int? count = null,
        string? detail = null,
        string? label = null,
        string? conflict = null)
    {
        Dictionary<string, string>? args = null;
        if (count is { } n) (args ??= [])["count"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (detail is { } d) (args ??= [])["detail"] = d;
        if (label is { } l) (args ??= [])["label"] = l;
        if (conflict is { } c) (args ??= [])["conflict"] = c;
        return new SyncEvent(_clock.UtcNow, kind, outcome, path, args);
    }
}
