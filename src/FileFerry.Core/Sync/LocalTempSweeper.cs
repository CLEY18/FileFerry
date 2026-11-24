using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync;

/// <summary>
/// Sweep stage for interrupted-write fragments (end of the run, alongside the buffer purge):
/// a temporary file the writing process never renamed — the device died mid-download — would
/// otherwise pile up in the pair's tree, and the planner (which excludes the fragment shape from
/// sync state) would hide it forever. The pass works only from what the run's own local scan
/// saw, and a delete failure leaves the fragment for the next run; a cancellation stops the
/// sweep itself, never the finished run's results.
/// No stash copy: a fragment is not user content but the app's own unfinished transaction —
/// its bytes are not trustworthy, and the real source (the server row or the local original)
/// is untouched.
/// </summary>
public static class LocalTempSweeper
{
    /// <summary>
    /// One sweep pass: deletes every scanned file whose name has the fragment shape, outside the
    /// deletion buffer; returns how many fragments left the disk.
    /// </summary>
    /// <param name="local">The pair's local side.</param>
    /// <param name="stash">Buffer policy — copies inside it keep their names and are not swept.</param>
    /// <param name="scanned">The run's local scan (fragments included — they are filtered later, not here).</param>
    /// <param name="ct">Run cancellation (the rest waits for the next pass).</param>
    public static async Task<int> SweepAsync(
        ILocalFileSystem local,
        IStashPolicy stash,
        IReadOnlyList<FileSnapshot> scanned,
        CancellationToken ct)
    {
        var swept = 0;
        foreach (var entry in scanned)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory
                || entry.Path.StartsWith(stash.StashRoot)
                || !LocalTempNames.IsFragment(entry.Path.Name))
            {
                continue;
            }

            var deleted = await local.DeleteAsync(entry.Path, ct).ConfigureAwait(false);
            if (deleted.IsSuccess)
            {
                swept++;
            }
        }

        return swept;
    }
}
