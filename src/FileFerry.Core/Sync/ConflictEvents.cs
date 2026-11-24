using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// The one counting rule for every surface that shows conflicts (pair card badge, pair
/// details, conflicts screen): a Warning event with a path is a conflict row, and a later
/// keep-side resolution of the same path acknowledges it — the row turns into its "kept …"
/// status and stops counting. Screens that count their own way diverge the moment a
/// resolution lands; they all call here instead.
/// </summary>
public static class ConflictEvents
{
    /// <summary>
    /// Whether this run-log event reads as an open conflict row.
    /// </summary>
    public static bool IsConflictRow(SyncEvent e) =>
        e.Outcome == SyncEventOutcome.Warning && e.Path is { IsEmpty: false };

    /// <summary>
    /// Last resolution moment per path — a conflict older than its path's resolution is acknowledged.
    /// </summary>
    public static Dictionary<RelativePath, SyncEvent> Resolutions(IReadOnlyList<SyncRunLog> runs) =>
        runs
            .SelectMany(r => r.Events)
            .Where(e => e.Kind == EventKind.ConflictResolved && e.Path is { IsEmpty: false })
            .GroupBy(static e => e.Path!)
            .ToDictionary(static g => g.Key, static g => g.MaxBy(static e => e.At)!);

    /// <summary>
    /// Conflicts of the run window that no resolution has acknowledged yet (the badge count).
    /// </summary>
    public static int CountUnresolved(IReadOnlyList<SyncRunLog> runs)
    {
        var resolved = Resolutions(runs);
        return runs.Sum(r => r.Events.Count(
            e => IsConflictRow(e)
                && (!resolved.TryGetValue(e.Path!, out var resolution) || resolution.At <= e.At)));
    }
}
