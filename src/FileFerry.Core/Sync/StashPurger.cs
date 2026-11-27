using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync;

/// <summary>
/// TTL-purge stage of the deletion buffer as part of the run (after operations are applied).
/// The trash-buffer screen promises "deletes in N days" — this is where the promise is kept: expired copies vanish
/// from disk and index. Safety rule: only what lies <em>under the stash root</em> is deleted — a
/// corrupt index row can never touch the pair's content (the invariant protects content; the
/// copies themselves are slated for destruction after TTL).
/// A delete failure leaves the row until the next run; a cancelled run simply doesn't finish
/// purging the rest. Time comes only from <see cref="IClock"/> (determinism).
/// </summary>
public static class StashPurger
{
    /// <summary>
    /// One purge pass: returns the number of index rows removed (the file was deleted or already
    /// missing — the index is healed in both cases).
    /// </summary>
    /// <param name="pairId">The pair whose stash-index rows the pass covers.</param>
    /// <param name="local">The pair's local side (copies live within the root).</param>
    /// <param name="index">Copy index (<c>stash_meta</c>).</param>
    /// <param name="policy">Policy: TTL + buffer root.</param>
    /// <param name="clock">Time source — the only "now" allowed (determinism).</param>
    /// <param name="ct">Run cancellation (leaves the rest for the next pass).</param>
    public static async Task<int> PurgeExpiredAsync(
        Guid pairId,
        ILocalFileSystem local,
        IStashRepository index,
        IStashPolicy policy,
        IClock clock,
        CancellationToken ct)
    {
        var entries = await index.ListAsync(pairId, ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return 0;
        }

        var now = clock.UtcNow;
        var root = policy.StashRoot;
        var purged = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            // outside the buffer — not our data: keep the row (don't multiply garbage by deleting)
            if (!entry.StashPath.StartsWith(root) || entry.StashPath == root)
            {
                continue;
            }

            if (!policy.IsExpired(entry.CreatedAt, now))
            {
                continue;
            }

            var deleted = await local.DeleteAsync(entry.StashPath, ct).ConfigureAwait(false);
            if (deleted.IsFailure && deleted.Error.Kind != ErrorKind.NotFound)
            {
                continue; // the provider said "no" — retry next run
            }

            await index.RemoveAsync(pairId, entry.StashPath, ct).ConfigureAwait(false);
            purged++;
        }

        return purged;
    }

    /// <summary>
    /// One orphan pass: collects buffer copies of deleted pairs whose TTL has ended.
    /// Same rules as the live pass — inside the buffer only, expired only, a provider refusal
    /// defers to the next boot — plus one of its own: a tree whose access is gone (revoked
    /// grant) keeps its rows untouched; the sweep never rewrites an address it cannot prove.
    /// </summary>
    /// <param name="orphans">Orphaned indexes (<see cref="IStashRepository.ListOrphansAsync"/>).</param>
    /// <param name="localForTree">Local side by tree key; <see langword="null"/> — the grant is gone.</param>
    /// <param name="index">Copy index; rows are removed only after the copy is confirmed gone.</param>
    /// <param name="policy">Policy: TTL + buffer root (shared with the live pass).</param>
    /// <param name="clock">Time source — the only "now" allowed (determinism).</param>
    /// <param name="ct">Pass cancellation (the rest is the next boot's job).</param>
    public static async Task<int> PurgeOrphansAsync(
        IReadOnlyList<OrphanedStashEntry> orphans,
        Func<string, ILocalFileSystem?> localForTree,
        IStashRepository index,
        IStashPolicy policy,
        IClock clock,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var root = policy.StashRoot;
        var purged = 0;
        foreach (var entry in orphans)
        {
            ct.ThrowIfCancellationRequested();

            // same shield as the live pass: outside the buffer stands the user's own data
            if (!entry.StashPath.StartsWith(root) || entry.StashPath == root)
            {
                continue;
            }

            if (!policy.IsExpired(entry.CreatedAt, now))
            {
                continue; // the promise window is open: the copy must stay retrievable
            }

            var local = localForTree(entry.TreeKey);
            if (local is null)
            {
                continue; // the tree is unreachable — the row waits for a boot that can reach it
            }

            var deleted = await local.DeleteAsync(entry.StashPath, ct).ConfigureAwait(false);
            if (deleted.IsFailure && deleted.Error.Kind != ErrorKind.NotFound)
            {
                continue; // the provider said "no" — retry on the next pass
            }

            await index.RemoveAsync(entry.PairId, entry.StashPath, ct).ConfigureAwait(false);
            purged++;
        }

        return purged;
    }
}
