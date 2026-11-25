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
        var quarantined = new List<RelativePath>();
        var quarantinedIncoming = new List<RelativePath>();
        var now = context.Now;

        // ── 1. Rename: two-part baseline↔side matching by evidence ──
        EmitMoves(emissions, context, pending, propagateToRemote: true);
        EmitMoves(emissions, context, pending, propagateToRemote: false);

        // ── 1.5. One-sided adds whose receiving side already holds another case of the path ──
        // The write would land on the twin: the case-insensitive device aliases (the provider
        // refuses or silently overwrites), the case-sensitive server grows a second collection —
        // either way every later run repeats the failure. A twin that is itself a fresh add on
        // the other side is not this shape — step 2 pairs those and files keep both versions.
        // No copy exists for such a note, so the row resolves by rename/delete like a folder one.
        var oneSidedAdds = pending
            .Where(static c => c.Kind == ChangeKind.Added && (c.Local is null) != (c.Remote is null))
            .ToList();
        var addedPaths = oneSidedAdds.Select(static c => c.Path).ToHashSet();
        foreach (var add in oneSidedAdds)
        {
            if (!pending.Contains(add))
            {
                continue; // consumed as a pair by a collision of its parent
            }

            var toRemote = add.Local is not null;
            var incoming = toRemote ? add.Local! : add.Remote!;
            var receivingSide = toRemote ? context.Remote : context.Local;
            if (receivingSide.Keys.Any(key =>
                    !Equals(key, incoming.Path) && key.EqualsIgnoreCase(incoming.Path) && !addedPaths.Contains(key)))
            {
                pending.Remove(add);
                conflicts.Add(new ConflictNote(ConflictKind.CaseCollision, add.Path, Folder: true));
                quarantinedIncoming.Add(add.Path);
            }
        }

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

            if (IsCoveredBy(localAdd.Path, quarantinedIncoming) || IsCoveredBy(rival.Path, quarantinedIncoming))
            {
                continue; // inside an incoming-versus-existing collision — its row covers this pair
            }

            pending.Remove(localAdd);
            pending.Remove(rival);

            if (localAdd.Local!.IsDirectory || rival.Remote!.IsDirectory)
            {
                // a folder cannot be duplicated under a conflict name (copies are file-level) and
                // its container cannot cross blindly (on a case-insensitive side the twin mkdir
                // aliases onto the same folder): no dead operations — the note says a side must
                // be renamed by hand. The quarantine covers the whole subtree: a child under the
                // colliding path has no destination until the rename lands (the twin collection
                // does not exist — or worse, the server aliases it onto the rival), and children
                // of their own would plan copy rows without surviving copies
                conflicts.Add(new ConflictNote(ConflictKind.CaseCollision, localAdd.Path, Folder: true));
                quarantined.Add(localAdd.Path);
                quarantined.Add(rival.Path);
                continue;
            }

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
                case ChangeKind.Added when change.Local is not null || change.Remote is not null:
                    // an ordinary add whose receiving side already holds the same spelling in a
                    // different case would land on the twin: the case-insensitive device aliases
                    // (the provider refuses or overwrites), the case-sensitive server grows a
                    // second collection — either way every later run repeats the failure. No copy
                    // exists for such a note, so the row resolves by rename/delete like a folder one
                    var toRemote = change.Local is not null;
                    var incoming = toRemote ? change.Local! : change.Remote!;
                    if (HasCaseTwin(incoming.Path, toRemote ? context.Remote : context.Local))
                    {
                        conflicts.Add(new ConflictNote(ConflictKind.CaseCollision, change.Path, Folder: true));
                        quarantinedIncoming.Add(change.Path);
                        break;
                    }

                    emissions.Add(WriteOp(incoming, toRemote));
                    break;

                case ChangeKind.Modified:
                    // DiffEngine guarantees: exactly one side changed
                    var localChanged = !change.Base!.SameContentAs(change.Local!);
                    var source = localChanged ? change.Local! : change.Remote!;
                    if (source.IsDirectory && change.Base.IsDirectory)
                    {
                        // a folder's content lives in its children — their own changes converge
                        // the pair, and the scan backfill heals the row's evidence; a folder
                        // "write" over an existing container would be churn at best
                        break;
                    }

                    emissions.Add(WriteOp(source, toRemote: localChanged));
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

                    // a container delete-vs-modify is not a content conflict: the strategy only
                    // recreates the folder, the children decide their own fates — nothing was kept
                    // or lost silently, so there is nothing for the conflicts screen to announce
                    var containerConflict =
                        (change.Local is null && change.Remote is { IsDirectory: true }) ||
                        (change.Remote is null && change.Local is { IsDirectory: true });
                    if (!containerConflict)
                    {
                        var folderNote = change.Local?.IsDirectory == true || change.Remote?.IsDirectory == true;
                        conflicts.Add(new ConflictNote(
                            change.Local is null || change.Remote is null ? ConflictKind.DeleteVersusModify : ConflictKind.BothModified,
                            change.Path,
                            Folder: folderNote));
                        if (folderNote)
                        {
                            quarantined.Add(change.Path); // the folder side's children cannot cross onto a file (or under a clashing twin)
                        }
                    }

                    break;
            }
        }

        // The quarantine is absolute: whatever pointed inside a folder-conflict subtree (child
        // writes, nested collision copies, nested rows) leaves the plan — the note covers the
        // subtree, and the next run replans from state once the rename action dissolves the shape.
        // An incoming-versus-existing collision covers its own path too: the add itself is the
        // operation that would alias; the existing twin's subtree stays fully plannable.
        var outside = emissions.Where(e =>
                !IsQuarantined(e.Op.Path, quarantined) && !IsCoveredBy(e.Op.Path, quarantinedIncoming))
            .ToList();
        var visible = conflicts.Where(c =>
                !IsQuarantined(c.Path, quarantined)
                && !IsQuarantined(c.Path, quarantinedIncoming)
                && !IsQuarantinedIgnoringCase(c.Path, quarantinedIncoming))
            .ToList();

        return new PlanningResult(Order(outside), visible);
    }

    /// <summary>
    /// Whether this path sits strictly below a quarantined incoming prefix in any case — the
    /// nested twins of one collision (both spellings unsynced) read as the parent row alone.
    /// </summary>
    private static bool IsQuarantinedIgnoringCase(RelativePath path, List<RelativePath> prefixes) =>
        prefixes.Any(prefix =>
            path.ToString().StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the receiving side already holds this path in another case — the alias shape.
    /// </summary>
    private static bool HasCaseTwin(RelativePath path, IReadOnlyDictionary<RelativePath, FileSnapshot> side) =>
        side.Keys.Any(key => !Equals(key, path) && key.EqualsIgnoreCase(path));

    /// <summary>
    /// The prefix itself or anything strictly below it.
    /// </summary>
    private static bool IsCoveredBy(RelativePath path, List<RelativePath> prefixes) =>
        prefixes.Any(prefix => Equals(path, prefix) || path.StartsWith(prefix));

    /// <summary>
    /// Strictly below one of the quarantined prefixes (a prefix itself is the conflict path,
    /// not a child of it).
    /// </summary>
    private static bool IsQuarantined(RelativePath path, List<RelativePath> prefixes) =>
        prefixes.Any(prefix => !Equals(path, prefix) && path.StartsWith(prefix));

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

        // a baseline record "missing from the side" → candidate source; mirrored — candidate target.
        // Evidence may live in different spaces on the two rows (the server speaks ETag, the device
        // only a content hash the hatch produced) — matching therefore compares rows pairwise,
        // hash when both have one, server tag when both have one. Bare size never matches; the
        // hatch only ever hashed rows inside the same-size window, so pairwise stays cheap.
        var sources = baseline
            .Where(kv => (!target.TryGetValue(kv.Key, out var t) || !kv.Value.SameContentAs(t))
                && !kv.Value.IsDirectory
                && (kv.Value.ETag is not null || kv.Value.ContentHash is not null))
            .OrderBy(static kv => kv.Key)
            .ToList();
        var targets = target
            .Where(kv => (!baseline.TryGetValue(kv.Key, out var b) || !kv.Value.SameContentAs(b))
                && !kv.Value.IsDirectory
                && (kv.Value.ETag is not null || kv.Value.ContentHash is not null))
            .OrderBy(static kv => kv.Key)
            .ToList();

        var moves = new List<(RelativePath Old, RelativePath New, FileSnapshot After)>();
        var consumedPaths = new HashSet<RelativePath>();

        // per-source candidates: same size, shared evidence, the opposite side untouched at both
        // paths (otherwise it's a conflict zone, not a rename), no Both-changed on either path
        var claims = new List<(KeyValuePair<RelativePath, FileSnapshot> Source, KeyValuePair<RelativePath, FileSnapshot> Target)>();
        foreach (var source in sources)
        {
            if (!MatchesBaseline(other, baseline, source.Key)
                || (changesByPath.TryGetValue(source.Key, out var oldChange) && oldChange.Kind == ChangeKind.Both))
            {
                continue;
            }

            foreach (var candidate in targets)
            {
                if (Equals(candidate.Key, source.Key)
                    || candidate.Value.Size != source.Value.Size
                    || !SameMoveEvidence(source.Value, candidate.Value)
                    || !MatchesBaseline(other, baseline, candidate.Key)
                    || (changesByPath.TryGetValue(candidate.Key, out var newChange) && newChange.Kind == ChangeKind.Both))
                {
                    continue;
                }

                claims.Add((source, candidate));
            }
        }

        // "the same content" must be proven on both sides: a source with two candidate targets or
        // a target two sources claim is an unproven guess — no move (a chain stays unambiguous:
        // each row has its own content, and TopoSort orders the moves that free each other's targets)
        var claimsPerSource = claims.GroupBy(static c => c.Source.Key).ToDictionary(static g => g.Key, static g => g.ToList());
        var claimsPerTarget = claims.GroupBy(static c => c.Target.Key).ToDictionary(static g => g.Key, static g => g.ToList());
        foreach (var claim in claims.DistinctBy(static c => c.Source.Key))
        {
            if (claimsPerSource[claim.Source.Key].Count != 1 || claimsPerTarget[claim.Target.Key].Count != 1)
            {
                continue;
            }

            var (source, newPair) = claim;
            // The projected row reads the device's clock and the server's tag together: the device
            // mtime (so its next scan still says "unchanged") plus the evidence the remote scan
            // will report at the destination. Inheriting one side's bare mtime would make the
            // other side "change" on the next run and re-transfer the moved file.
            static FileSnapshot AfterEvidence(RelativePath path, FileSnapshot deviceRow, FileSnapshot? remoteEvidence) =>
                new(path, deviceRow.Kind, deviceRow.Size, deviceRow.ModifiedAt,
                    remoteEvidence?.ETag ?? deviceRow.ETag,
                    deviceRow.ContentHash ?? remoteEvidence?.ContentHash);

            var after = propagateToRemote
                ? AfterEvidence(
                    newPair.Key,
                    context.Local.TryGetValue(newPair.Key, out var deviceRow) ? deviceRow : source.Value,
                    context.Remote.TryGetValue(source.Key, out var serverSource) ? serverSource : null)
                : AfterEvidence(
                    newPair.Key,
                    context.Local.TryGetValue(source.Key, out var deviceOld) ? deviceOld : source.Value,
                    context.Remote.TryGetValue(newPair.Key, out var serverTarget) ? serverTarget : null);
            moves.Add((source.Key, newPair.Key, after));
            consumedPaths.Add(source.Key);
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
    /// Whether two rows prove "the same content" through a shared evidence space: the content
    /// hash when both carry one, the server tag when both carry one. A row pair living in
    /// different spaces (device hash versus server tag) is unproven — that shape is exactly
    /// what the evidence hatch hashes before planning, never compared blind here.
    /// </summary>
    private static bool SameMoveEvidence(FileSnapshot from, FileSnapshot to) =>
        from.ContentHash is { } fh && to.ContentHash is { } th
            ? string.Equals(fh, th, StringComparison.Ordinal)
            : from.ETag is { } fe && to.ETag is { } te && string.Equals(fe, te, StringComparison.Ordinal);

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
