using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Retry;

/// <summary>
/// Retry settings (Options pattern): exponential backoff with <b>full jitter</b>
/// (AWS practice — a uniform spread over 0..cap softens the "stamped" effect after a server outage).
/// </summary>
public sealed record RetryPolicyOptions
{
    /// <summary>
    /// Base delay of the first retry.
    /// </summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Cap on a single delay (no growth beyond it).
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of <em>retries</em> after the first attempt.
    /// </summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>
    /// Total delay budget per call — exceeded ⇒ the failure propagates upward.
    /// </summary>
    public TimeSpan MaxTotalDelay { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Sleep abstraction — introduced for deterministic timing tests (no real delays).
/// </summary>
public interface ITaskDelay
{
    /// <summary>
    /// Wait for <paramref name="delay"/>; cancellation propagates straight through.
    /// </summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>
/// Production implementation — <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// </summary>
public sealed class TaskDelay : ITaskDelay
{
    /// <inheritdoc cref="ITaskDelay.DelayAsync"/>
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>
/// Decorator over <see cref="IWritableRemoteStore"/>: retries only <em>transient</em> failures
/// (<see cref="SyncError.IsTransient"/>: network/server) with exponential backoff + full jitter.
/// Everything else (auth, conflict, permission) returns immediately — a retry cannot fix it.
/// </summary>
public sealed class RetryingRemoteStore : IWritableRemoteStore
{
    private readonly IWritableRemoteStore _inner;
    private readonly RetryPolicyOptions _options;
    private readonly ITaskDelay _delay;
    private readonly Random _jitter;

    /// <param name="inner">Wrapped store (the WebDAV adapter).</param>
    /// <param name="options">Backoff parameters (base delay, cap, attempt budget).</param>
    /// <param name="delay">Wait mechanics (a fake in tests).</param>
    /// <param name="jitter">Spread source (a fixed seed in tests).</param>
    public RetryingRemoteStore(IWritableRemoteStore inner, RetryPolicyOptions options, ITaskDelay delay, Random? jitter = null)
    {
        _inner = inner;
        _options = options;
        _delay = delay;
        _jitter = jitter ?? Random.Shared;
    }

    /// <summary>
    /// Delay of retry number <paramref name="attempt"/>: uniform(0, min(cap, base·2^attempt)).
    /// </summary>
    /// <param name="options">Backoff parameters (base delay and per-retry cap).</param>
    /// <param name="attempt">Index of the attempt that preceded the retry (0-based).</param>
    /// <param name="unit">Uniform 0..1 draw from the jitter source.</param>
    public static TimeSpan NextDelay(RetryPolicyOptions options, int attempt, double unit)
    {
        var capMs = Math.Min(options.MaxDelay.TotalMilliseconds, options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(unit * capMs);
    }

    /// <inheritdoc cref="IRemoteBrowser.ListAsync"/>
    public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct) =>
        WithRetryAsync(token => _inner.ListAsync(folder, token), ct);

    /// <inheritdoc cref="IRemoteBrowser.ReadAsync"/>
    public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) =>
        WithRetryAsync(token => _inner.ReadAsync(file, token), ct);

    /// <inheritdoc cref="IRemoteWriter.WriteAsync"/>
    public async Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct) =>
        await WithRetryAsync(async token =>
        {
            Rewind(content); // before each retry — otherwise the second PUT would send an empty body
            return await _inner.WriteAsync(file, content, token).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc cref="IRemoteWriter.CreateFolderAsync"/>
    public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct) =>
        WithRetryAsync(token => _inner.CreateFolderAsync(folder, token), ct);

    /// <inheritdoc cref="IRemoteMover.MoveAsync"/>
    public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) =>
        WithRetryAsync(token => _inner.MoveAsync(from, to, token), ct);

    /// <inheritdoc cref="IRemoteDeleter.DeleteAsync"/>
    public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) =>
        WithRetryAsync(token => _inner.DeleteAsync(path, token), ct);

    /// <summary>
    /// "Call → transient failure → jittered delay → retry" loop with a total-sleep budget.
    /// </summary>
    private async Task<Result<T, SyncError>> WithRetryAsync<T>(Func<CancellationToken, Task<Result<T, SyncError>>> call, CancellationToken ct)
    {
        var spent = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            var result = await call(ct).ConfigureAwait(false);
            if (result.IsSuccess || !result.Error.IsTransient || attempt >= _options.MaxAttempts)
            {
                return result;
            }

            var next = NextDelay(_options, attempt, _jitter.NextDouble());
            if (spent + next > _options.MaxTotalDelay)
            {
                return result;
            }

            spent += next;
            await _delay.DelayAsync(next, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rewinds the stream to the start (restarting a repeatable PUT).
    /// </summary>
    private static void Rewind(Stream content)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }
    }
}
