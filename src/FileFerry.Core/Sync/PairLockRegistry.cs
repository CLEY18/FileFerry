using System.Collections.Concurrent;

namespace FileFerry.Core.Sync;

/// <summary>
/// Keyed-lock registry for pairs (invariant): one pair — one active run; different pairs
/// run in parallel. Semantics are "busy → refuse", not a queue: a repeat trigger during an active
/// run is redundant (WorkManager unique-REPLACE already deduplicates), and a queue would hide
/// cancellations.
/// </summary>
public sealed class PairLockRegistry
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Try to acquire the pair's lock immediately; <see langword="null"/> — a run is already active.
    /// Semaphores stay in the registry (pairs are few, the leak is immaterial) — no removal race.
    /// </summary>
    /// <returns>A disposable for <c>using</c>-release, or <see langword="null"/>.</returns>
    public IDisposable? TryAcquire(Guid pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var gate = _locks.GetOrAdd(pairId, static _ => new SemaphoreSlim(1, 1));
        return gate.Wait(TimeSpan.Zero) ? new Releaser(gate) : null;
    }

    /// <summary>
    /// Release handle: returns one signal to the semaphore on <see cref="Dispose"/>.
    /// </summary>
    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
