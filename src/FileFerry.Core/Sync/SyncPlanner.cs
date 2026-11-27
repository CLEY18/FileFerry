using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Composition of the pipeline's pure part (scan results → Diff → Plan) into one call — the point
/// where a "partial scan failure" stops the run before any operations (catalog scenario).
/// Scan IO and execution stay outside: SyncRun supplies the keyed lock, journal, and baseline UoW.
/// </summary>
public sealed class SyncPlanner
{
    private readonly DiffEngine _diff = new();
    private readonly MergePlanner _planner;
    private readonly IStashPolicy _stash;

    /// <summary>
    /// Create a facade over the planner with the given strategies.
    /// </summary>
    /// <param name="planner">The policy stage (rename matching + change rules) this facade delegates to.</param>
    /// <param name="stash">Stash policy — its root is excluded from scans (the buffer isn't synced).</param>
    public SyncPlanner(MergePlanner planner, IStashPolicy stash)
    {
        _planner = planner;
        _stash = stash;
    }

    /// <summary>
    /// Side scan results (already in pair coordinates) + baseline → plan.
    /// Any scan failure propagates: no run, no operations, the baseline doesn't move.
    /// </summary>
    /// <param name="pair">The folder pair — goes verbatim into the resulting <see cref="SyncRunContext"/>.</param>
    /// <param name="localScan">Local scan (pair coordinates); the stash subtree is dropped before Diff.</param>
    /// <param name="remoteScan">Remote scan (pair coordinates); not stash-filtered — the buffer is local.</param>
    /// <param name="baseline">The loaded baseline; stale stash rows are filtered out of it too.</param>
    /// <param name="now">The run moment from <c>IClock</c>.</param>
    public Result<PlanningResult, SyncError> Plan(
        FolderPair pair,
        Result<IReadOnlyList<FileSnapshot>, SyncError> localScan,
        Result<IReadOnlyList<FileSnapshot>, SyncError> remoteScan,
        IReadOnlyList<FileSnapshot> baseline,
        DateTimeOffset now)
    {
        if (localScan.IsFailure)
        {
            return Result<PlanningResult, SyncError>.Failure(localScan.Error);
        }

        if (remoteScan.IsFailure)
        {
            return Result<PlanningResult, SyncError>.Failure(remoteScan.Error);
        }

        var collisions = new HashSet<RelativePath>();
        var local = SnapshotIndex.From(WithoutLocalNoise(localScan.Value), collisions);
        var remote = SnapshotIndex.From(remoteScan.Value, collisions);
        var baseIndex = SnapshotIndex.From(WithoutLocalNoise(baseline), collisions);
        var changes = _diff.Diff(local, remote, baseIndex);
        var context = new SyncRunContext(pair, now, local, remote, baseIndex);
        var plan = _planner.Plan(context, changes);
        return Result<PlanningResult, SyncError>.Success(
            plan with { NameCollisions = [.. collisions.OrderBy(static p => p.ToString(), StringComparer.Ordinal)] });
    }

    /// <summary>
    /// Drops the local-only noise from the diff inputs: the stash-buffer subtree (pre-deletion
    /// copies are not sync state) and interrupted-write fragments ("<c>name.tmp-&lt;id&gt;</c>" —
    /// the app's own litter, never user content; the sweep at the end of the run clears them).
    /// </summary>
    private IReadOnlyList<FileSnapshot> WithoutLocalNoise(IReadOnlyList<FileSnapshot> snapshots) =>
        snapshots.Where(static s => !LocalTempNames.IsFragment(s.Path.Name)).Where(s => !s.Path.StartsWith(_stash.StashRoot)).ToArray();
}
