using FileFerry.Core.Domain;

namespace FileFerry.Core.Ports;

/// <summary>
/// Time source — for deterministic tests (the determinism invariant) and consistent "own" time in the journal.
/// </summary>
public interface IClock
{
    /// <summary>
    /// Current UTC instant from the injected source — the engine never reads the system clock directly.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Content hash computation (the engine is not tied to SHA-256 — details live in the App adapter).
/// </summary>
public interface IHashProvider
{
    /// <summary>
    /// Hex hash of bytes (lowercase).
    /// </summary>
    string Hash(ReadOnlySpan<byte> data);

    /// <summary>
    /// Hash of a stream (handles large files without buffering in memory).
    /// </summary>
    /// <param name="content">Stream, positioned from the start by the adapter.</param>
    /// <param name="ct">Cancels the streaming read.</param>
    Task<string> HashAsync(Stream content, CancellationToken ct);
}

/// <summary>
/// Secret storage (Keystore/SecureStorage in the App). The only place the app token lives
/// (invariant).
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Read a value by logical key; <see langword="null"/> — key absent.
    /// </summary>
    /// <param name="key">Logical key (contains no secrets).</param>
    /// <param name="ct">Cancels the store read.</param>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>
    /// Save/overwrite a value.
    /// </summary>
    /// <param name="key">Logical key (contains no secrets).</param>
    /// <param name="value">Secret; never lands in logs.</param>
    /// <param name="ct">Cancels the store write.</param>
    Task SetAsync(string key, string value, CancellationToken ct);

    /// <summary>
    /// Delete a secret (account sign-out).
    /// </summary>
    Task RemoveAsync(string key, CancellationToken ct);
}

/// <summary>
/// "Is the service reachable" probe (status.php + timeout) — a cheaper signal than a full run.
/// </summary>
public interface IAvailabilityProbe
{
    /// <summary>
    /// One server-status probe; network unavailability → <see cref="ErrorKind.ServerGone"/>.
    /// </summary>
    Task<Result<ServerStatus, SyncError>> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// status.php reply, simplified to what the engine/UI needs.
/// </summary>
/// <param name="Version">Server version (for diagnostics and capabilities).</param>
/// <param name="MaintenanceMode">Server in maintenance mode — runs pause.</param>
public sealed record ServerStatus(string Version, bool MaintenanceMode);

/// <summary>
/// Background job registration (WorkManager in the App). Policy lives in Core; this is mechanics only.
/// </summary>
public interface ISyncScheduler
{
    /// <summary>
    /// Set/update the pair's unique periodic job (unique name → REPLACE).
    /// </summary>
    /// <param name="pairId">Target pair — scopes the unique periodic job.</param>
    /// <param name="schedule">New schedule (Manual — cancels the job).</param>
    /// <param name="ct">Cancels the registration call.</param>
    Task ScheduleAsync(Guid pairId, SyncSchedule schedule, CancellationToken ct);

    /// <summary>
    /// One-off "Now" run (manual sync) — runs even a paused pair: the pause guards the schedule,
    /// not the owner's direct order.
    /// </summary>
    Task RunNowAsync(Guid pairId, CancellationToken ct);

    /// <summary>
    /// Remove the pair's job (pair deleted / paused).
    /// </summary>
    Task CancelAsync(Guid pairId, CancellationToken ct);
}

/// <summary>
/// Structured log level (ILogSink) — no "fatal": critical = Error + a journal event.
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// Diagnostics detail (enabled in dev only).
    /// </summary>
    Debug,

    /// <summary>
    /// Normal flow.
    /// </summary>
    Info,

    /// <summary>
    /// Anomaly that was compensated (retry etc.).
    /// </summary>
    Warning,

    /// <summary>
    /// Failure visible to the user.
    /// </summary>
    Error,
}

/// <summary>
/// Structured logging. Input is controlled: no tokens/passwords (invariant).
/// </summary>
public interface ILogSink
{
    /// <summary>
    /// Emit one structured record; level, message and exception must all be secret-free.
    /// </summary>
    void Write(LogLevel level, string message, Exception? exception = null);
}
