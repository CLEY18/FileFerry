namespace FileFerry.Core.Domain;

/// <summary>
/// Unit marker for <see cref="Result{TValue,TError}"/> — success with no data.
/// </summary>
public readonly record struct Ok;

/// <summary>
/// Classification of <em>expected</em> pipeline failures. Each has its own layer reaction:
/// retry (network), re-authentication (token), UI notification (conflict), deferral (disk).
/// </summary>
public enum ErrorKind
{
    /// <summary>
    /// App token invalid/revoked — a new Login Flow is required.
    /// </summary>
    AuthExpired,

    /// <summary>
    /// Server unreachable (status.php did not answer) — the run is deferred.
    /// </summary>
    ServerGone,

    /// <summary>
    /// Temporary network failure — retry with backoff.
    /// </summary>
    Network,

    /// <summary>
    /// No entry at the given path.
    /// </summary>
    NotFound,

    /// <summary>
    /// Storage full — retry after space is freed.
    /// </summary>
    StorageFull,

    /// <summary>
    /// Access denied (SAF permission lost, 403).
    /// </summary>
    PermissionLost,

    /// <summary>
    /// State blocked the operation (target exists, version race) — the planner resolves it.
    /// </summary>
    Conflict,

    /// <summary>
    /// Everything else; details in <see cref="SyncError.Message"/>. Never secrets!
    /// </summary>
    Unknown,
}

/// <summary>
/// Description of an expected failure (instead of exceptions for everyday situations).
/// <remarks>Invariant the message must never contain a token/password — it gets written to logs.</remarks>
/// </summary>
/// <param name="Kind">Failure class driving the layer reaction.</param>
/// <param name="Message">Human-readable fact without secrets.</param>
/// <param name="Cause">Root-cause exception (for tracing), optional.</param>
public sealed record SyncError(ErrorKind Kind, string Message, Exception? Cause = null)
{
    /// <summary>
    /// Whether the failure deserves backoff retry (transient: network/server). Strategies do not "soften" the rest.
    /// </summary>
    public bool IsTransient => Kind is ErrorKind.Network or ErrorKind.ServerGone;

    /// <summary>
    /// Factory shorthand for <c>new SyncError(…)</c>; the message follows the same no-secrets rule.
    /// </summary>
    public static SyncError Of(ErrorKind kind, string message, Exception? cause = null) => new(kind, message, cause);

    /// <summary>
    /// Message for UI/log (secrets can't be here — SyncError convention).
    /// </summary>
    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>
/// The pipeline returns success with a value <b>or</b> failure with <typeparamref name="TError"/> — no exceptions
/// for everyday situations. Value and error are readable only under the matching <see cref="IsSuccess"/>.
/// Success means <see cref="IsSuccess"/> == <see langword="true"/>.
/// </summary>
/// <typeparam name="TValue">Success payload; <see cref="Ok"/> for results carrying no data.</typeparam>
/// <typeparam name="TError">Failure type (usually <see cref="SyncError"/>).</typeparam>
public readonly struct Result<TValue, TError>
{
    private readonly TValue? _value;
    private readonly TError? _error;

    private Result(bool isSuccess, TValue? value, TError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    /// <summary>
    /// Success flag; gates <see cref="Value"/> — check it before reading either side.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Negation of <see cref="IsSuccess"/>; gates <see cref="Error"/>.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Success value; throws <see cref="InvalidOperationException"/> on failure (a programming error).
    /// </summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The result is a failure — Value is unavailable; check IsSuccess.");

    /// <summary>
    /// The failure; throws on success (likewise a bug in caller logic).
    /// </summary>
    public TError Error => IsSuccess
        ? throw new InvalidOperationException("The result is a success — Error is unavailable.")
        : _error!;

    /// <summary>
    /// Success result carrying <paramref name="value"/> verbatim (read it back via <see cref="Value"/>).
    /// </summary>
    public static Result<TValue, TError> Success(TValue value) => new(true, value, default);

    /// <summary>
    /// Success result with no data (for <c>Result&lt;Ok, …&gt;</c>).
    /// </summary>
    public static Result<TValue, TError> Success() => new(true, default, default);

    /// <summary>
    /// Failure result carrying <paramref name="error"/> verbatim (read it back via <see cref="Error"/>).
    /// </summary>
    public static Result<TValue, TError> Failure(TError error) => new(false, default, error);

    /// <summary>
    /// Convenience: a value can be returned directly where a Result is expected.
    /// </summary>
    public static implicit operator Result<TValue, TError>(TValue value) => Success(value);

    /// <summary>
    /// Tracing output for debugging (no secrets — by data convention).
    /// </summary>
    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({_error})";
}
