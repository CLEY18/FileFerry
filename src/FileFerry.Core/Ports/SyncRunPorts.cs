namespace FileFerry.Core.Ports;

/// <summary>
/// Ready "environment" of one pair run: aggregate + sides + probe. The composition root
/// (App/Worker) builds it from configuration and secrets; Core does not know how adapters
/// are created (DIP).
/// </summary>
/// <param name="Pair">Pair — the aggregate keying the run.</param>
/// <param name="Local">Local side, bound to <c>FolderPair.LocalTreeKey</c>.</param>
/// <param name="Remote">Remote side (usually already wrapped in a retry decorator).</param>
/// <param name="Probe">Availability probe of the pair account's server — a cheap run gatekeeper.</param>
public sealed record SyncRunScope(
    Domain.FolderPair Pair,
    ILocalFileSystem Local,
    IWritableRemoteStore Remote,
    IAvailabilityProbe Probe);

/// <summary>
/// Factory of <see cref="SyncRunScope"/> by pair id: fetches configuration and the token 
/// <see cref="ISecretStore"/> and assembles the adapters. A failed open (no pair/account/token) is
/// an expected <see cref="Domain.SyncError"/>, not an exception.
/// </summary>
public interface ISyncScopeFactory
{
    /// <summary>
    /// Assemble the run environment; a configuration error → failure with no side effects.
    /// </summary>
    Task<Domain.Result<SyncRunScope, Domain.SyncError>> OpenAsync(Guid pairId, CancellationToken ct);
}

/// <summary>
/// Observer of run progress ("SyncStatusService": Worker → UI without Messenger).
/// Calls come from the run's flow thread, synchronously and cheaply; the implementation only
/// publishes state. Engine determinism stays untouched: the observer influences no decision
/// (invariant).
/// </summary>
public interface ISyncRunObserver
{
    /// <summary>
    /// Step totals are known (after planning or journal resume) — exactly once per run.
    /// </summary>
    /// <param name="pairId">The running pair (progress is routed per pair).</param>
    /// <param name="totalSteps">Total steps (0 — empty plan).</param>
    void OnPlanned(Guid pairId, int totalSteps);

    /// <summary>
    /// One step applied (journal: → Applied); totals are <c>totalSteps</c> on success.
    /// </summary>
    void OnStepApplied(Guid pairId);
}
