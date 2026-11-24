using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Pure pipeline function: <c>(baseline, local, remote) → Change[]</c>.
/// No IO, no time, no operation decisions — only a three-way assessment of state.
/// The "Diff" pipeline step picks no strategy: a conflict stays <see cref="ChangeKind.Both"/> for the planner.
/// </summary>
public sealed class DiffEngine
{
    /// <summary>
    /// Computes changes against the baseline for every path present in at least one set.
    /// </summary>
    /// <param name="local">Local-side snapshot (keyed by pair path).</param>
    /// <param name="remote">Remote-side snapshot (keyed by pair path).</param>
    /// <param name="baseline">Last agreed state — the third input of the three-way assessment.</param>
    /// <returns>Changes in a deterministic order (by depth, then ordinal).</returns>
    public IReadOnlyList<Change> Diff(
        IReadOnlyDictionary<RelativePath, FileSnapshot> local,
        IReadOnlyDictionary<RelativePath, FileSnapshot> remote,
        IReadOnlyDictionary<RelativePath, FileSnapshot> baseline)
    {
        var changes = new List<Change>();
        foreach (var path in local.Keys.Union(remote.Keys).Union(baseline.Keys).OrderBy(static p => p))
        {
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            baseline.TryGetValue(path, out var b);

            var change = Classify(path, b, l, r, local, remote, baseline);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        return changes;
    }

    /// <summary>
    /// Three-way assessment of a single path; <see langword="null"/> — no changes (or both sides
    /// match the baseline).
    /// </summary>
    private static Change? Classify(
        RelativePath path, FileSnapshot? b, FileSnapshot? l, FileSnapshot? r,
        IReadOnlyDictionary<RelativePath, FileSnapshot> local,
        IReadOnlyDictionary<RelativePath, FileSnapshot> remote,
        IReadOnlyDictionary<RelativePath, FileSnapshot> baseIndex)
    {
        // Added: not in the baseline.
        if (b is null)
        {
            return (l, r) switch
            {
                (not null, null) => new Change(ChangeKind.Added, path, Base: null, Local: l, Remote: null),
                (null, not null) => new Change(ChangeKind.Added, path, Base: null, Local: null, Remote: r),
                // both sides added the directory — children define its "content", always silent here;
                // but there is no row — Converged so RecordConvergence pins it into the baseline
                (not null, not null) when l!.IsDirectory && r!.IsDirectory =>
                    new Change(ChangeKind.Converged, path, Base: null, Local: l, Remote: r),
                // both sides added the file: silent merge only on strong evidence (hash/etag) —
                // and seed the row via Converged, without it the engine "doesn't remember" the file:
                // a later one-sided delete reads as "new on the server" and would resurrect it
                (not null, not null) when l!.SameByStrongEvidence(r) =>
                    new Change(ChangeKind.Converged, path, Base: null, Local: l, Remote: r),
                (not null, not null) => new Change(ChangeKind.Both, path, Base: null, Local: l, Remote: r),
                _ => null, // gone from all three — none of our business
            };
        }

        // Was in the baseline: assess each side separately (changed/deleted/unchanged).
        // A folder's content lives in its children: a mute row (the engine writes baseline folder
        // rows without collection evidence until a scan heals one) must never say "unchanged"
        // while the side's subtree gained or moved entries — for a directory the children are
        // the evidence, and propagating a deletion over unseen content is exactly what this gates.
        var localChanged = l is null || !b.SameContentAs(l) || l.IsDirectory && !ChildrenMatch(local, baseIndex, path);
        var remoteChanged = r is null || !b.SameContentAs(r) || r.IsDirectory && !ChildrenMatch(remote, baseIndex, path);

        // Both sides gone — simply dropped from the baseline later; no operations.
        if (l is null && r is null)
        {
            return null;
        }

        // Neither side deviated from the baseline — silent pair (checked before strict evidence:
        // otherwise a local side without an ETag would forever look "modified" — churn).
        if (!localChanged && !remoteChanged)
        {
            return null;
        }

        // A side changed its type under the same name (a file replaced a synced folder or the
        // reverse): no operation carries that across without destroying the other kind —
        // a conflict for hand resolution, not a blind write.
        if ((l is not null && l.Kind != b.Kind) || (r is not null && r.Kind != b.Kind))
        {
            return new Change(ChangeKind.Both, path, b, l, r);
        }

        // Only local deviated from the baseline → one-sided change (converge toward remote).
        if (localChanged && !remoteChanged)
        {
            return new Change(l is null ? ChangeKind.Deleted : ChangeKind.Modified, path, b, l, r);
        }

        // Only remote deviated → one-sided change (converge toward local).
        if (remoteChanged && !localChanged)
        {
            return new Change(r is null ? ChangeKind.Deleted : ChangeKind.Modified, path, b, l, r);
        }

        // One side deleted, the other modified — a "delete-vs-modify" conflict:
        // the only live content must survive (strategy policy); both-null is unreachable here.
        if (l is null || r is null)
        {
            return new Change(ChangeKind.Both, path, b, l, r);
        }

        // Both sides changed: directories — silent (children decide); files — a match only on
        // strong evidence (equal size is no longer "same content"), otherwise — a conflict.
        if (l.IsDirectory && r.IsDirectory)
        {
            return null;
        }

        return l.SameByStrongEvidence(r) ? null : new Change(ChangeKind.Both, path, b, l, r);
    }

    /// <summary>
    /// Whether a directory's subtree on one side still matches the baseline: the same set of
    /// descendant entries, each pair content-equal per <see cref="FileSnapshot.SameContentAs"/>.
    /// Deep changes count through every ancestor; entries the baseline never saw (or vanished)
    /// mean the folder moved even when its own row carries no evidence to say so.
    /// </summary>
    private static bool ChildrenMatch(
        IReadOnlyDictionary<RelativePath, FileSnapshot> side,
        IReadOnlyDictionary<RelativePath, FileSnapshot> baseline,
        RelativePath folder)
    {
        var sideCount = 0;
        foreach (var entry in side)
        {
            if (entry.Key.Equals(folder) || !entry.Key.StartsWith(folder))
            {
                continue;
            }

            sideCount++;
            if (!baseline.TryGetValue(entry.Key, out var known) || !entry.Value.SameContentAs(known))
            {
                return false;
            }
        }

        foreach (var key in baseline.Keys)
        {
            if (!key.Equals(folder) && key.StartsWith(folder) && !side.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }
}
