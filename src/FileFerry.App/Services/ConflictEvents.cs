using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.App.Services;

/// <summary>
/// One rule for both surfaces that count conflicts (pair card badge, conflicts screen):
/// a Warning event with a path is a conflict row, and a later keep-side resolution of the same
/// path acknowledges it — the row turns into its "kept …" status and stops counting.
/// </summary>
internal static class ConflictEvents
{
    /// <summary>
    /// Whether this run-log event reads as an open conflict row.
    /// </summary>
    internal static bool IsConflictRow(SyncEvent e) =>
        e.Outcome == SyncEventOutcome.Warning && e.Path is { IsEmpty: false };

    /// <summary>
    /// Last resolution moment per path — a conflict older than its path's resolution is acknowledged.
    /// </summary>
    internal static Dictionary<RelativePath, SyncEvent> Resolutions(IReadOnlyList<SyncRunLog> runs) =>
        runs
            .SelectMany(r => r.Events)
            .Where(e => e.Kind == EventKind.ConflictResolved && e.Path is { IsEmpty: false })
            .GroupBy(static e => e.Path!)
            .ToDictionary(static g => g.Key, static g => g.MaxBy(static e => e.At)!);

    /// <summary>
    /// Conflicts of the run window that no resolution has acknowledged yet (the badge count).
    /// </summary>
    internal static int CountUnresolved(IReadOnlyList<SyncRunLog> runs)
    {
        var resolved = Resolutions(runs);
        return runs.Sum(r => r.Events.Count(
            e => IsConflictRow(e)
                && (!resolved.TryGetValue(e.Path!, out var resolution) || resolution.At <= e.At)));
    }
}
