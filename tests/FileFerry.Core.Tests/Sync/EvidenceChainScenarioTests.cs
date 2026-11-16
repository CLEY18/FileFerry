using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Evidence chain for "the same content": silent merge only on <b>strong</b> evidence
/// (hash/etag); size by itself is not evidence but grounds for a KeepBoth conflict.
/// Every row is a real case (v4/v5, 31 B) or a guardrail
/// against the opposite failure (churn).
/// </summary>
[Trait("Category", "SyncScenario")]
public sealed class EvidenceChainScenarioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
    private static readonly FolderPair Pair = new(Guid.Empty, Guid.Empty, "Sync", "tree", "Sync");

    // ── "merged silently" — only on strong evidence ────────────────────────────────

    [Fact]
    public void AddAdd_SameSizeDifferentHash_ConflictsKeepBoth()
    {
        var plan = Plan(
            local: [File("a.txt", 31, hash: "h1")],
            remote: [File("a.txt", 31, hash: "h2")],
            baseline: []);

        Assert.Equal(3, plan.Operations.Count); // copy-down + copy-up + convergence of the primary path
        Assert.Equal(ConflictKind.BothModified, Assert.Single(plan.Conflicts).Kind);
    }

    [Fact]
    public void AddAdd_SameSizeNoEvidence_NotSilent()
    {
        // Bare size does not prove identity: the safe default is a conflict (the SyncRun hasher adds evidence).
        var plan = Plan(
            local: [File("a.txt", 31)],
            remote: [File("a.txt", 31)],
            baseline: []);

        Assert.NotEmpty(plan.Operations);
        Assert.Single(plan.Conflicts);
    }

    [Fact]
    public void AddAdd_SameHash_Silent()
    {
        var plan = Plan(
            local: [File("a.txt", 31, hash: "h")],
            remote: [File("a.txt", 31, hash: "h")],
            baseline: []);

        // "Silent" = no content moved and no conflicts; since the convergence work a no-op
        // RecordConvergence is added so the shared state leaves a trace in the baseline (otherwise a delete "resurrects").
        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.RecordConvergence, op.Kind);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void AddAdd_BothDirectories_Silent()
    {
        // A directory has no "content" to prove with — its children decide it; an over-strict rule must not seed conflicts.
        var plan = Plan(
            local: [Dir("docs")],
            remote: [Dir("docs")],
            baseline: []);

        // Seed a baseline row for the directory too (without it a folder delete would lack three-way footing).
        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.RecordConvergence, op.Kind);
        Assert.Empty(plan.Conflicts);
    }

    // ── both sides changed ───────────────────────────────────────────────────

    [Fact]
    public void BothChanged_SameHash_Silent()
    {
        var plan = Plan(
            local: [File("a.txt", 9, hash: "h")],
            remote: [File("a.txt", 9, hash: "h")],
            baseline: [File("a.txt", 9, hash: "hb")]);

        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void BothChanged_SameSizeDifferentHash_Conflicts()
    {
        var plan = Plan(
            local: [File("a.txt", 9, hash: "hl")],
            remote: [File("a.txt", 9, hash: "hr")],
            baseline: [File("a.txt", 9, hash: "hb")]);

        Assert.Equal(3, plan.Operations.Count);
        Assert.Single(plan.Conflicts);
    }

    [Fact]
    public void BothChanged_LegacyRowNoEtag_BothEditedAtDifferentTimes_Conflicts()
    {
        // A real case: baseline row without an ETag (legacy, pre-fix), v4 on the server and
        // v5 local — both 31 B, both sides' mtimes later than the baseline. Staying silent is forbidden.
        var plan = Plan(
            local: [File("Test.md", 31, modifiedAt: T1)],
            remote: [File("Test.md", 31, modifiedAt: T2, etag: "e5")],
            baseline: [File("Test.md", 31, modifiedAt: T0)]);

        Assert.NotEmpty(plan.Operations);
        Assert.Single(plan.Conflicts);
    }

    // ── one-sided changes: mtime of the same coordinate side is a signal ─────────

    [Fact]
    public void LocalEdit_SameSize_MtimeDiffers_Uploads()
    {
        // The editor rewrote the file without changing its size; the remote side is confirmed quiet (same ETag).
        var plan = Plan(
            local: [File("a.txt", 5, modifiedAt: T1)],
            remote: [File("a.txt", 5, modifiedAt: T0, etag: "e1")],
            baseline: [File("a.txt", 5, modifiedAt: T0, etag: "e1")]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.UploadToRemote, op.Kind);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void ModifiedRemote_EtagChangedSizeEqual_Downloads()
    {
        var plan = Plan(
            local: [File("a.txt", 5, modifiedAt: T0)],
            remote: [File("a.txt", 5, modifiedAt: T1, etag: "e2")],
            baseline: [File("a.txt", 5, modifiedAt: T0, etag: "e1")]);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.DownloadToLocal, op.Kind);
    }

    [Fact]
    public void Unchanged_SameSizeSameMtime_Quiet()
    {
        // Guard against the opposite failure: same size+mtime on the same coordinate side — the pair is quiet.
        var plan = Plan(
            local: [File("a.txt", 5, modifiedAt: T0)],
            remote: [File("a.txt", 5, modifiedAt: T0, etag: "e1")],
            baseline: [File("a.txt", 5, modifiedAt: T0, etag: "e1")]);

        Assert.Empty(plan.Operations);
        Assert.Empty(plan.Conflicts);
    }

    // ── what counts as evidence inside FileSnapshot itself ───────────────────────────────────

    [Fact]
    public void SameByStrongEvidence_RequiresHashOrEtag_NotSize()
    {
        var bare = new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, T0);
        var sameSize = new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, T0);
        var hashed = bare with { ContentHash = "h" };

        Assert.False(bare.SameByStrongEvidence(sameSize)); // size is not evidence
        Assert.False(bare.SameByStrongEvidence(hashed)); // one side without evidence — not evidence
        Assert.True(hashed.SameByStrongEvidence(hashed with { }));
        Assert.True(
            new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, ETag: "E").SameByStrongEvidence(
                new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, ETag: "E")));
    }

    [Fact]
    public void SameContentAs_WeakBranch_UsesMtimeOfSameCoordinateSide()
    {
        var baseline = new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, T0);
        var editedLater = new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, T1);
        var untouched = new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 31, T0.AddMilliseconds(500));

        Assert.False(baseline.SameContentAs(editedLater)); // mtime moved — content is suspect
        Assert.True(baseline.SameContentAs(untouched)); // second-granularity on DAV/SAF
        Assert.True(baseline.SameContentAs(bare(baseline))); // mtime unknown — size remains
    }

    private static FileSnapshot bare(FileSnapshot s) => s with { ModifiedAt = null };

    // ── pipeline facade and snapshot constructors ────────────────────────────────────

    /// <summary>
    /// Runs the three sets through the real pipeline (Diff → Plan), as in ScenarioCatalogTests.
    /// </summary>
    private static PlanningResult Plan(
        IReadOnlyList<FileSnapshot> local,
        IReadOnlyList<FileSnapshot> remote,
        IReadOnlyList<FileSnapshot> baseline)
    {
        var stash = new StashPolicy(new StashPolicyOptions());
        var planner = new SyncPlanner(new MergePlanner(new KeepBothStrategy(), stash), stash);
        var result = planner.Plan(
            Pair,
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(local),
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(remote),
            baseline,
            T0);

        Assert.True(result.IsSuccess, "the pipeline must not derail");
        return result.Value;
    }

    private static FileSnapshot File(
        string path, long size, DateTimeOffset? modifiedAt = null, string? etag = null, string? hash = null) =>
        new(RelativePath.Parse(path), EntryKind.File, size, modifiedAt, etag, hash);

    private static FileSnapshot Dir(string path) =>
        new(RelativePath.Parse(path), EntryKind.Directory, 0);
}
