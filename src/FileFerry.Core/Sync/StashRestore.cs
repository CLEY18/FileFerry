using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync;

/// <summary>
/// Puts one stash-buffer copy back at its origin path. Restoring is an overwrite write, so it
/// obeys the deletion rule: a busy origin is never destroyed unbuffered. The origin name may have
/// been refilled after the copy was made — the server delivered it again on a later run, or the
/// user created it by hand — so the current content moves into the buffer (a fresh stamp, an
/// index row like any copy) before the restore lands. The restored copy itself stays in the
/// buffer until its retention ends, which keeps a half-finished restore retryable.
/// </summary>
public static class StashRestore
{
    /// <summary>
    /// Restores one copy: buffer the busy origin first, then copy the stash file out.
    /// A failure anywhere leaves nothing half-written: the copy refuses, the origin keeps
    /// its content, and the stash row waits for a retry.
    /// </summary>
    /// <param name="local">The pair's local side (stash-indexed in the run scope, so buffered copies register themselves).</param>
    /// <param name="stash">Buffer policy — the safety copy takes the same layout the planner gives its copies.</param>
    /// <param name="clock">Time source (names the safety copy's stamp — the only "now" allowed).</param>
    /// <param name="stashPath">The copy to restore (inside the buffer root).</param>
    /// <param name="originPath">Where it belongs (pair coordinates).</param>
    /// <param name="ct">Caller cancellation; a stopped restore is a not-happened restore.</param>
    public static async Task<Result<Ok, SyncError>> RestoreAsync(
        ILocalFileSystem local,
        IStashPolicy stash,
        IClock clock,
        RelativePath stashPath,
        RelativePath originPath,
        CancellationToken ct)
    {
        var exists = await local.ExistsAsync(originPath, ct).ConfigureAwait(false);
        if (exists.IsFailure)
        {
            return Result<Ok, SyncError>.Failure(exists.Error);
        }

        if (exists.Value)
        {
            var buffered = await local
                .CopyAsync(originPath, stash.StashDestination(originPath, clock.UtcNow), ct)
                .ConfigureAwait(false);
            if (buffered.IsFailure)
            {
                // the current content stays at risk nowhere: the refuse leaves it in place,
                // and the retry buffers it again under a fresh stamp
                return Result<Ok, SyncError>.Failure(buffered.Error);
            }
        }

        var restored = await local.CopyAsync(stashPath, originPath, ct).ConfigureAwait(false);
        return restored.IsSuccess
            ? Result<Ok, SyncError>.Success()
            : Result<Ok, SyncError>.Failure(restored.Error);
    }
}
