using FileFerry.App.Services;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using FileFerry.Data;

namespace FileFerry.App;

/// <summary>
/// SAF-backed orphan sweep: opens each tree an orphaned copy lives in by its persisted
/// grant and hands the collection to the shared purge rules (buffer-only, expired-only).
/// A tree whose grant was revoked stays untouched — a row survives for a boot that can
/// reach it again, and an unreachable failure never breaks the start.
/// </summary>
public sealed class AndroidOrphanedCopySweeper : IOrphanedCopySweeper
{
    private readonly IStashRepository _index;
    private readonly IStashPolicy _policy;
    private readonly IClock _clock;

    /// <summary>
    /// Assembles the sweeper from the copy index, the shared stash policy and the clock.
    /// </summary>
    public AndroidOrphanedCopySweeper(IStashRepository index, IStashPolicy policy, IClock clock)
    {
        _index = index;
        _policy = policy;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task SweepAsync(CancellationToken ct)
    {
        var sides = new Dictionary<string, ILocalFileSystem?>();
        try
        {
            var orphans = await _index.ListOrphansAsync(ct).ConfigureAwait(false);
            if (orphans.Count == 0)
            {
                return;
            }

            var resolver = Android.App.Application.Context.ContentResolver;
            if (resolver is null)
            {
                return;
            }

            await StashPurger.PurgeOrphansAsync(
                orphans,
                key => sides.TryGetValue(key, out var cached)
                    ? cached
                    : GetOrOpen(resolver, key),
                _index, _policy, _clock, ct).ConfigureAwait(false);

            ILocalFileSystem? GetOrOpen(
                Android.Content.ContentResolver content, string treeKey)
            {
                var treeUri = Android.Net.Uri.Parse(treeKey);
                ILocalFileSystem? side = null;
                if (treeUri is not null
                    && content.PersistedUriPermissions.Any(p => treeUri.Equals(p.Uri)))
                {
                    side = new SafLocalFileSystem(content, treeUri);
                }

                sides[treeKey] = side;
                return side;
            }
        }
        catch (Exception)
        {
            // the sweep is a courtesy, not a gate: whatever it did not finish, the next start finishes
        }
    }
}
