using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Domain;

/// <summary>
/// Event outcome for coloring a log row — status is never conveyed by color alone.
/// </summary>
public enum SyncEventOutcome
{
    /// <summary>
    /// Neutral fact.
    /// </summary>
    Info,

    /// <summary>
    /// Action succeeded.
    /// </summary>
    Success,

    /// <summary>
    /// Conflict/warning — yellow, not red.
    /// </summary>
    Warning,

    /// <summary>
    /// Failure — needs attention.
    /// </summary>
    Error,
}

/// <summary>
/// Log event type: stored in the DB as a number, the text is rendered on display in the App
/// via AppStrings — the log does not depend on the language at write time.
/// New values are appended at the end (stability: the DB stores an int).
/// </summary>
public enum EventKind
{
    /// <summary>
    /// Run started.
    /// </summary>
    RunStarted,

    /// <summary>
    /// Run cancelled (schedule/app stopped).
    /// </summary>
    RunCancelled,

    /// <summary>
    /// Recovery of an interrupted run from the journal; argument <c>count</c>.
    /// </summary>
    ResumeStopped,

    /// <summary>
    /// Conflict detected (event with Path); argument <c>conflict</c> — the conflict kind.
    /// </summary>
    ConflictDetected,

    /// <summary>
    /// Plan composed; argument <c>count</c> — number of operations.
    /// </summary>
    PlanSummary,

    /// <summary>
    /// Applied and baseline updated; argument <c>count</c>.
    /// </summary>
    AppliedBaseline,

    /// <summary>
    /// Run stopped by a step-error (quarantine); argument <c>detail</c> — the raw "what".
    /// </summary>
    RunStopped,

    /// <summary>
    /// A stage failed; arguments <c>label</c> (phrase key) + <c>detail</c> (raw error text).
    /// </summary>
    RunFailed,

    /// <summary>
    /// Gray text as a self-contained fact (argument <c>detail</c>): diagnostics without a fixed phrase.
    /// </summary>
    FreeText,

    /// <summary>
    /// The TTL-purge stage removed expired copies; argument <c>count</c>.
    /// </summary>
    StashPurged,

    /// <summary>
    /// A keep-side resolution closed a conflict on <see cref="SyncEvent.Path"/>;
    /// argument <c>side</c> — "local" or "server".
    /// </summary>
    ConflictResolved,

    /// <summary>
    /// The scan carried <see cref="SyncEvent.Path"/> in several Unicode spellings of one name;
    /// the run keeps one of them while the provider holds duplicates.
    /// </summary>
    NameFormCollision,
}

/// <summary>
/// One run-log event — "raw material" for the Journal screen. An event is intent only:
/// <see cref="Kind"/> (required) + <see cref="Args"/>; the App renders the text from AppStrings.
/// No secrets in arguments.
/// </summary>
/// <param name="At">Event time per <see cref="Ports.IClock"/>.</param>
/// <param name="Kind">Event type — determines the rendered phrase.</param>
/// <param name="Outcome">Severity class for log coloring; never the sole status cue.</param>
/// <param name="Path">Related pair entry; <see langword="null"/> for run-level events.</param>
/// <param name="Args">Phrase parameters (<c>count</c>/<c>detail</c>/…); value texts without secrets.</param>
public sealed record SyncEvent(
    DateTimeOffset At,
    EventKind Kind,
    SyncEventOutcome Outcome = SyncEventOutcome.Info,
    RelativePath? Path = null,
    IReadOnlyDictionary<string, string>? Args = null);

/// <summary>
/// Run summary for the history list.
/// </summary>
public enum SyncRunOutcome
{
    /// <summary>
    /// Currently running.
    /// </summary>
    Running,

    /// <summary>
    /// All operations applied.
    /// </summary>
    Success,

    /// <summary>
    /// Some operations not applied (will continue on the next run).
    /// </summary>
    Partial,

    /// <summary>
    /// Run interrupted (network/cancel) — state stayed consistent.
    /// </summary>
    Failed,

    /// <summary>
    /// Cancelled by user/WorkManager.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Record of one run: identity, time bounds, outcome, and events.
/// Written through ISyncStateRepository (UoW).
/// </summary>
/// <param name="RunId">Unique id of this run (history-row identity).</param>
/// <param name="PairId">The pair this run synced (baseline and lock key).</param>
/// <param name="StartedAt">Start per IClock.</param>
/// <param name="FinishedAt">Finish; <see langword="null"/> — still running.</param>
/// <param name="Outcome">Run outcome; <see cref="SyncRunOutcome.Running"/> while <see cref="FinishedAt"/> is null.</param>
/// <param name="Events">Run events in chronological order.</param>
public sealed record SyncRunLog(
    Guid RunId,
    Guid PairId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    SyncRunOutcome Outcome,
    IReadOnlyList<SyncEvent> Events);

/// <summary>
/// State of an operation's write-ahead-journal record (resume mechanics).
/// </summary>
public enum JournalState
{
    /// <summary>
    /// Operation planned, not yet executed — on resume a repeat is <em>possible</em>.
    /// </summary>
    Planned,

    /// <summary>
    /// Applied — a re-run skips it (resume idempotency).
    /// </summary>
    Applied,

    /// <summary>
    /// Failure with the reason in the journal; the baseline is not moved.
    /// </summary>
    Failed,
}

/// <summary>
/// Write-ahead journal record: sequence number + operation + its state at the moment of writing.
/// </summary>
/// <param name="Seq">Monotonic per-pair number — recovery order.</param>
/// <param name="Operation">The planned operation this record guards (resume replays it).</param>
/// <param name="State">State at write time — decides skip-or-repeat on resume.</param>
/// <param name="At">Write time per IClock.</param>
public sealed record JournalEntry(
    long Seq,
    SyncOperation Operation,
    JournalState State,
    DateTimeOffset At);
