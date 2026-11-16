using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Pure policy function: <c>(context, Change[]) → SyncOperation[]</c> with topological
/// order (directories before children; move chains free the target first; deletions leaf-down).
/// Invariants: any deletion ⟹ a prior copy in the stash; determinism on (input, now).
/// </summary>
public sealed class MergePlanner
{
    private readonly IConflictResolutionStrategy _conflicts;
    private readonly IStashPolicy _stash;

    /// <summary>
    /// Create a planner with the given strategies (DI default: KeepBoth + StashPolicy).
    /// </summary>
    public MergePlanner(IConflictResolutionStrategy conflicts, IStashPolicy stash)
    {
        _conflicts = conflicts;
        _stash = stash;
    }

    /// <summary>
    /// Plans the run's operations: first rename matching across the whole tree, then direct change rules.
    /// </summary>
    /// <param name="context">The run context; its <c>Now</c> names conflict/stash copies (determinism).</param>
    /// <param name="changes">Output of <see cref="DiffEngine"/>.</param>
    public PlanningResult Plan(SyncRunContext context, IReadOnlyList<Change> changes)
    {
        var pending = changes.ToList();
        var conflicts = new List<ConflictNote>();
        var emissions = new List<PlannedOp>();
        var now = context.Now;

        // ── 1. Rename: two-part baseline↔side matching by evidence ──
        EmitMoves(emissions, context, pending, propagateToRemote: true);
        EmitMoves(emissions, context, pending, propagateToRemote: false);

        // ── 2. Case-collision between local-added and remote-added ──
        var adds = pending.Where(static c => c.Kind == ChangeKind.Added).ToArray();
        foreach (var localAdd in adds.Where(static c => c.Remote is null && c.Local is not null))
        {
            if (!pending.Contains(localAdd))
            {
                continue; // path already consumed by another collision
            }

            var rival = adds.FirstOrDefault(c =>
                pending.Contains(c) && c.Remote is not null && c.Local is null
                && !Equals(c.Path, localAdd.Path) && c.Path.EqualsIgnoreCase(localAdd.Path));
            if (rival is null)
            {
                continue; // no rival — an ordinary one-sided add
            }

            pending.Remove(localAdd);
            pending.Remove(rival);

            var copy = _conflicts.ConflictCopyName(localAdd.Path, now);
            emissions.Add(new PlannedOp(Phase.ConflictCopy, copy.Depth, new SyncOperation(OperationKind.CopyLocal, copy, localAdd.Path, localAdd.Local! with { Path = copy })));
            emissions.Add(new PlannedOp(Phase.Write, copy.Depth, new SyncOperation(OperationKind.UploadToRemote, copy, null, localAdd.Local! with { Path = copy })));
            emissions.Add(new PlannedOp(Phase.Write, rival.Path.Depth, new SyncOperation(OperationKind.DownloadToLocal, rival.Path, null, rival.Remote!)));
            conflicts.Add(new ConflictNote(ConflictKind.CaseCollision, localAdd.Path));
        }

        // ── 3. Remaining changes — direct rules ──
        foreach (var change in pending)
        {
            switch (change.Kind)
            {
                case ChangeKind.Added when change.Local is not null:
                    emissions.Add(WriteOp(change.Local, toRemote: true));
                    break;

                case ChangeKind.Added when change.Remote is not null:
                    emissions.Add(WriteOp(change.Remote, toRemote: false));
                    break;

                case ChangeKind.Modified:
                    // DiffEngine guarantees: exactly one side changed
                    var localChanged = !change.Base!.SameContentAs(change.Local!);
                    emissions.Add(WriteOp(localChanged ? change.Local! : change.Remote!, toRemote: localChanged));
                    break;

                case ChangeKind.Deleted:
                    EmitDeleteThroughStash(change, now, emissions);
                    break;

                case ChangeKind.Converged:
                    // A state shared by strong evidence but without a row — a no-op operation that
                    // passes the journal and seeds the baseline like any other applied operation.
                    // Evidence = local coordinate snapshot ⊕ remote evidence (as after Upload).
                    var shared = change.Local!;
                    var evidence = shared with
                    {
                        ETag = change.Remote!.ETag ?? shared.ETag,
                        ContentHash = shared.ContentHash ?? change.Remote.ContentHash,
                    };
                    emissions.Add(new PlannedOp(Phase.Reconvene, change.Path.Depth,
                        new SyncOperation(OperationKind.RecordConvergence, change.Path, null, evidence)));
                    break;

                case ChangeKind.Both:
                    foreach (var operation in _conflicts.Resolve(change, now))
                    {
                        emissions.Add(new PlannedOp(PhaseOf(operation.Kind), operation.Path.Depth, operation));
                    }

                    conflicts.Add(new ConflictNote(
                        change.Local is null || change.Remote is null ? ConflictKind.DeleteVersusModify : ConflictKind.BothModified,
                        change.Path));
                    break;
            }
        }

        return new PlanningResult(Order(emissions), conflicts);
    }

    /// <summary>
    /// Finds a side's moves (baseline↔local matching for propagation to the server, and the
    /// mirror image) and removes the involved changes from further processing. A chain (a→b, b→c)
    /// yields a topological order.
    /// </summary>
    private static void EmitMoves(List<PlannedOp> emissions, SyncRunContext context, List<Change> pending, bool propagateToRemote)
    {
        var target = propagateToRemote ? context.Local : context.Remote;   // the side where the rename happened
        var other = propagateToRemote ? context.Remote : context.Local;    // the side we propagate to
        var baseline = context.Baseline;
        var changesByPath = pending.GroupBy(static c => c.Path).ToDictionary(static g => g.Key, static g => g.First());

        // a baseline record "missing from the side" by the same content → candidate source; mirrored — candidate target
        var sources = baseline
            .Where(kv => !target.TryGetValue(kv.Key, out var t) || !kv.Value.SameContentAs(t))
            .Where(kv => IdentityKey(kv.Value) is not null)
            .OrderBy(static kv => kv.Key)
            .ToList();
        var targets = target
            .Where(kv => !baseline.TryGetValue(kv.Key, out var b) || !kv.Value.SameContentAs(b))
            .Where(kv => IdentityKey(kv.Value) is not null)
            .OrderBy(static kv => kv.Key)
            .ToList();

        var sourceByKey = sources
            .GroupBy(kv => IdentityKey(kv.Value)!)
            .Where(static g => g.Count() == 1)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        var targetByKey = targets
            .GroupBy(kv => IdentityKey(kv.Value)!)
            .Where(static g => g.Count() == 1)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);

        var moves = new List<(RelativePath Old, RelativePath New, FileSnapshot After)>();
        var consumedPaths = new HashSet<RelativePath>();
        foreach (var (oldPath, sourceEntry) in sources)
        {
            var key = IdentityKey(sourceEntry)!;
            // the key must be unique on both sides (otherwise "the same content" isn't proven)
            if (!sourceByKey.TryGetValue(key, out var uniqueSource) || !ReferenceEquals(uniqueSource.Value, sourceEntry)
                || !targetByKey.TryGetValue(key, out var newPair) || Equals(newPair.Key, oldPath))
            {
                continue;
            }

            // a target taken by another rename source means a chain — allowed (TopoSort orders it);
            // the opposite side of both paths must match the baseline — otherwise it's a conflict zone, not a rename
            if (!MatchesBaseline(other, baseline, oldPath) || !MatchesBaseline(other, baseline, newPair.Key))
            {
                continue;
            }

            // a change on either path is a two-sided conflict → no move
            if ((changesByPath.TryGetValue(oldPath, out var oldChange) && oldChange.Kind == ChangeKind.Both)
                || (changesByPath.TryGetValue(newPair.Key, out var newChange) && newChange.Kind == ChangeKind.Both))
            {
                continue;
            }

            // after the move, the target side holds the source side's record under the new path
            var after = propagateToRemote
                ? (context.Remote.TryGetValue(oldPath, out var remoteEntry) ? remoteEntry : sourceEntry) with { Path = newPair.Key }
                : (context.Local.TryGetValue(oldPath, out var localEntry) ? localEntry : sourceEntry) with { Path = newPair.Key };
            moves.Add((oldPath, newPair.Key, after));
            consumedPaths.Add(oldPath);
            consumedPaths.Add(newPair.Key);
        }

        if (moves.Count == 0)
        {
            return;
        }

        foreach (var move in TopoSort(moves))
        {
            emissions.Add(new PlannedOp(Phase.Move, move.New.Depth,
                new SyncOperation(propagateToRemote ? OperationKind.MoveRemote : OperationKind.MoveLocal, move.New, move.Old, move.After)));
        }

        pending.RemoveAll(c => consumedPaths.Contains(c.Path));
    }

    /// <summary>
    /// Evidence key of "the same content": ETag, else ContentHash; bare size is not evidence.
    /// </summary>
    private static string? IdentityKey(FileSnapshot snapshot) => snapshot.ETag ?? snapshot.ContentHash;

    /// <summary>
    /// Whether the opposite side matches the baseline at a path (both absent — also a match).
    /// </summary>
    private static bool MatchesBaseline(
        IReadOnlyDictionary<RelativePath, FileSnapshot> other,
        IReadOnlyDictionary<RelativePath, FileSnapshot> baseline,
        RelativePath path)
    {
        other.TryGetValue(path, out var o);
        baseline.TryGetValue(path, out var b);
        return (o is null && b is null) || (o is not null && b is not null && b.SameContentAs(o));
    }

    /// <summary>
    /// Move toposort: a target is free once its previous occupant has moved; a cycle stays as-is
    /// (the executor reports Conflict).
    /// </summary>
    private static List<(RelativePath Old, RelativePath New, FileSnapshot After)> TopoSort(
        List<(RelativePath Old, RelativePath New, FileSnapshot After)> moves)
    {
        var ordered = new List<(RelativePath Old, RelativePath New, FileSnapshot After)>();
        var remaining = moves.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(m => remaining.All(other => !Equals(other.Old, m.New))).ToList();
            if (ready.Count == 0)
            {
                ordered.AddRange(remaining);
                break;
            }

            foreach (var move in ready)
            {
                ordered.Add(move);
                remaining.Remove(move);
            }
        }

        return ordered;
    }

    /// <summary>
    /// File → Upload/Download; directory → Create* (the creation phase comes first).
    /// </summary>
    private static PlannedOp WriteOp(FileSnapshot source, bool toRemote) => source.IsDirectory
        ? new PlannedOp(Phase.Create, source.Path.Depth,
            new SyncOperation(toRemote ? OperationKind.CreateRemoteFolder : OperationKind.CreateLocalFolder, source.Path, null, source))
        : new PlannedOp(Phase.Write, source.Path.Depth,
            new SyncOperation(toRemote ? OperationKind.UploadToRemote : OperationKind.DownloadToLocal, source.Path, null, source));

    /// <summary>
    /// Invariant any deletion is preceded by a copy in the stash (local by definition).
    /// If the record vanished locally, the copy is taken from the server
    /// (<see cref="OperationKind.DownloadToLocal"/> into the stash path).
    /// Directory exception: copying is file-level (SAF), and a directory's content is protected
    /// its children's own stash copies (each child is a separate change); an empty directory is
    /// not a byte array — deleting it cannot lose data.
    /// </summary>
    private void EmitDeleteThroughStash(Change change, DateTimeOffset now, List<PlannedOp> emissions)
    {
        var doomed = change.Local ?? change.Remote!;
        var deletedLocally = change.Local is null;
        if (!doomed.IsDirectory)
        {
            var stashPath = _stash.StashDestination(change.Path, now);
            var stashCopy = new SyncOperation(
                deletedLocally ? OperationKind.DownloadToLocal : OperationKind.CopyLocal,
                stashPath,
                change.Path,
                doomed with { Path = stashPath });
            emissions.Add(new PlannedOp(Phase.StashCopy, 0, stashCopy));
        }

        emissions.Add(new PlannedOp(Phase.Delete, change.Path.Depth,
            new SyncOperation(deletedLocally ? OperationKind.DeleteRemote : OperationKind.DeleteLocal, change.Path, null, doomed)));
    }

    /// <summary>
    /// Phase in the global order for an operation kind.
    /// </summary>
    private static int PhaseOf(OperationKind kind) => kind switch
    {
        OperationKind.CreateRemoteFolder or OperationKind.CreateLocalFolder => Phase.Create,
        OperationKind.MoveRemote or OperationKind.MoveLocal => Phase.Move,
        OperationKind.CopyLocal => Phase.ConflictCopy,
        OperationKind.UploadToRemote or OperationKind.DownloadToLocal => Phase.Write,
        _ => Phase.Delete,
    };

    /// <summary>
    /// Global stable order: phase → depth (deletions from the deepest) → emission order.
    /// </summary>
    private static IReadOnlyList<SyncOperation> Order(List<PlannedOp> emissions)
    {
        var index = 0;
        return emissions
            .Select(e => (e, order: index++))
            .OrderBy(static x => x.e.Phase)
            .ThenBy(static x => x.e.Phase == Phase.Delete ? -x.e.Depth : x.e.Depth)
            .ThenBy(static x => x.order)
            .Select(static x => x.e.Op)
            .ToList();
    }

    /// <summary>
    /// Execution-order phases (lower runs earlier).
    /// </summary>
    private static class Phase
    {
        /// <summary>
        /// Directory creation (parents before children).
        /// </summary>
        public const int Create = 0;

        /// <summary>
        /// Renames (topological: freeing targets first).
        /// </summary>
        public const int Move = 1;

        /// <summary>
        /// Conflict copies (before any overwrite).
        /// </summary>
        public const int ConflictCopy = 2;

        /// <summary>
        /// Content transfer.
        /// </summary>
        public const int Write = 3;

        /// <summary>
        /// Copy into the stash — after ordinary writes, before deletions.
        /// </summary>
        public const int StashCopy = 4;

        /// <summary>
        /// Deletions (leaves first).
        /// </summary>
        public const int Delete = 5;

        /// <summary>
        /// Convergences: touch neither side — after all real actions, in depth order.
        /// </summary>
        public const int Reconvene = 6;
    }

    /// <summary>
    /// A plan entry with its phase sort key.
    /// </summary>
    private readonly record struct PlannedOp(int Phase, int Depth, SyncOperation Op);
}
