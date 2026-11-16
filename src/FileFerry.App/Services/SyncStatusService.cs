using FileFerry.App.Resources;
using FileFerry.Core.Ports;

namespace FileFerry.App.Services;

/// <summary>
/// Instant state of the active run of a pair for the UI ("File 24 of 210").
/// </summary>
/// <param name="StartedAt">The moment the run started.</param>
/// <param name="AppliedSteps">Applied steps (observer ticks).</param>
/// <param name="TotalSteps">The total (<c>-1</c> — not announced by OnPlanned yet).</param>
/// <param name="Cancelling">Cancellation requested by the UI, not yet finished.</param>
public sealed record PairRunStatus(Guid PairId, DateTimeOffset StartedAt, int AppliedSteps, int TotalSteps, bool Cancelling)
{
    /// <summary>
    /// Progress-bar fraction (0..1); <see langword="null"/> — the total is unknown (indeterminate).
    /// </summary>
    public double? Ratio => TotalSteps > 0 ? Math.Clamp((double)AppliedSteps / TotalSteps, 0, 1) : null;

    /// <summary>
    /// Label under the bar: "Cancelling…" / "Preparing…" / "File X of Y".
    /// </summary>
    public string ProgressLabel => Cancelling
        ? AppStrings.RunCancelling
        : Ratio is null
            ? AppStrings.SyncPreparing
            : string.Format(AppStrings.SyncProgressFormat, AppliedSteps, TotalSteps);
}

/// <summary>
/// In-proc hub of run state (#3 /): Worker → <see cref="SyncStatusService"/> → UI via
/// the <see cref="StateChanged"/> event, without Messenger. DI singleton; parallel runs 
/// different pairs are distinguished by the pairId key. Events arrive on run flow threads — the
/// VM marshals them onto the UI thread.
/// </summary>
public sealed class SyncStatusService : ISyncRunObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _active = [];

    /// <summary>
    /// Any state change (start/tick/completion/cancellation); subscribers re-read <see cref="StatusFor"/>.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Register the active run of a pair (done by the Worker before <c>RunAsync</c>).
    /// </summary>
    /// <returns>Registration: a cancellation token from the UI + completion in <c>finally</c>.</returns>
    public RunRegistration Begin(Guid pairId)
    {
        var entry = new Entry(pairId, new CancellationTokenSource());
        lock (_gate)
        {
            _active[pairId] = entry; // a repeated trigger overwrites — the SyncRun lock will refuse the second run anyway
        }

        Fire();
        return new RunRegistration(this, entry);
    }

    /// <summary>
    /// Request cancellation of the active run of a pair (the "Cancel" button). Nothing active — false.
    /// </summary>
    public bool Cancel(Guid pairId)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(pairId, out var entry) || entry.Cancelling)
            {
                return false;
            }

            entry.Cancelling = true;
            entry.Cancel.Cancel();
        }

        Fire();
        return true;
    }

    /// <summary>
    /// State of the active run of a pair; <see langword="null"/> — there is no run.
    /// </summary>
    public PairRunStatus? StatusFor(Guid pairId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(pairId, out var e) ? e.ToStatus() : null;
        }
    }

    /// <inheritdoc />
    public void OnPlanned(Guid pairId, int totalSteps)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(pairId, out var entry))
            {
                entry.Total = totalSteps;
            }
        }

        Fire();
    }

    /// <inheritdoc />
    public void OnStepApplied(Guid pairId)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(pairId, out var entry))
            {
                entry.Applied++;
            }
        }

        Fire();
    }

    /// <summary>
    /// Unregister (the run finished) and notify the UI.
    /// </summary>
    internal void End(Entry entry)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(entry.PairId, out var current) && ReferenceEquals(current, entry))
            {
                _active.Remove(entry.PairId);
            }
        }

        entry.Cancel.Dispose();
        Fire();
    }

    private void Fire() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Mutable record of an active run (accessed only under <c>_gate</c>).
    /// </summary>
    internal sealed class Entry(Guid pairId, CancellationTokenSource cancel)
    {
        public Guid PairId { get; } = pairId;

        /// <summary>
        /// The start moment (fixed once; does not drift between reads).
        /// </summary>
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Cancellation source from the UI.
        /// </summary>
        public CancellationTokenSource Cancel { get; } = cancel;

        public int Applied;

        /// <summary>
        /// The total; -1 — OnPlanned has not happened yet.
        /// </summary>
        public int Total = -1;

        public bool Cancelling;

        public PairRunStatus ToStatus() => new(PairId, StartedAt, Applied, Total, Cancelling);
    }
}

/// <summary>
/// Handle of the active run for the Worker: cancellation token + completion (idempotent).
/// </summary>
public sealed class RunRegistration : IDisposable
{
    private readonly SyncStatusService _service;
    private readonly SyncStatusService.Entry _entry;
    private bool _done;

    internal RunRegistration(SyncStatusService service, SyncStatusService.Entry entry)
    {
        _service = service;
        _entry = entry;
    }

    /// <summary>
    /// Cancellation token from the UI — linked with the Worker's token.
    /// </summary>
    public CancellationToken Token => _entry.Cancel.Token;

    /// <summary>
    /// Finish the registration (after RunAsync; repeated calls are a noop).
    /// </summary>
    public void Dispose()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        _service.End(_entry);
    }
}
