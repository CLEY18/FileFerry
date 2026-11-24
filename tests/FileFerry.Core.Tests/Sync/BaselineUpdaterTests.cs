using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Sync;
using FileFerry.Core.Sync.Execution;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// <see cref="BaselineUpdater"/> — the baseline advances only via applied operations (one UoW);
/// stash copies and failed operations never change it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BaselineUpdaterTests
{
    private static readonly RelativePath Stash = RelativePath.Parse("FileFerry Trash");

    [Fact]
    public void Project_Upload_AdvancesEntryWithAfterSnapshot()
    {
        var after = Snap("a.txt", "v2", etag: "\"E2\"");
        var next = BaselineUpdater.Project(Index(Snap("a.txt", "v1")), [Op(OperationKind.UploadToRemote, "a.txt", after: after)], Stash);

        Assert.Equal("\"E2\"", Assert.Single(next).Value.ETag);
    }

    [Fact]
    public void Project_Delete_RemovesEntry_StashCopyIgnored()
    {
        var applied = new List<SyncOperation>
        {
            new(OperationKind.CopyLocal, RelativePath.Parse("FileFerry Trash/t1/a.txt"), RelativePath.Parse("a.txt"), Snap("FileFerry Trash/t1/a.txt", "v1")),
            new(OperationKind.DeleteLocal, RelativePath.Parse("a.txt")),
        };

        var next = BaselineUpdater.Project(Index(Snap("a.txt", "v1")), applied, Stash);

        Assert.Empty(next); // a stash copy is not state; a delete drops the entry
    }

    [Fact]
    public void Project_Move_RenamesEntryReusingEvidence()
    {
        var move = new SyncOperation(OperationKind.MoveRemote, RelativePath.Parse("b.txt"), RelativePath.Parse("a.txt"));

        var next = BaselineUpdater.Project(Index(Snap("a.txt", "v1", etag: "\"E1\"")), [move], Stash);

        var kept = Assert.Single(next);
        Assert.Equal("b.txt", kept.Key.ToString());
        Assert.Equal("\"E1\"", kept.Value.ETag);
    }

    [Fact]
    public async Task FailedRun_BaselineMovesOnlyForAppliedOps()
    {
        var pair = new FolderPair(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
        var clock = new MutableClock();
        var local = new InMemoryFileSystem(clock);
        var remote = new InMemoryRemoteStore(clock);
        remote.SeedCollection(RemotePath.Parse("sync")); // the wizard's folder exists before the first run
        var state = new InMemorySyncStateRepository();
        var stash = new StashPolicy(new StashPolicyOptions());

        await local.WriteAsync(
            RelativePath.Parse("a.txt"), new MemoryStream(Encoding.UTF8.GetBytes("A")), CancellationToken.None);

        var plan = new List<SyncOperation>
        {
            new(OperationKind.UploadToRemote, RelativePath.Parse("a.txt"), null, Snap("a.txt", "A")),
            new(OperationKind.UploadToRemote, RelativePath.Parse("missing.txt"), null, Snap("missing.txt", "M")),
        };

        var executor = new OperationExecutor(local, remote, state, stash, OperationExecutor.DefaultHandlers());
        var context = new SyncRunContext(
            pair, clock.UtcNow,
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>());

        var report = await executor.ExecuteAsync(context, plan, CancellationToken.None);
        await new BaselineUpdater(state, stash)
            .ApplyAsync(pair.Id, report.Applied, ct: CancellationToken.None);

        Assert.False(report.Completed);
        var baseline = await state.LoadBaselineAsync(pair.Id, CancellationToken.None);
        Assert.Equal(["a.txt"], baseline.Select(static s => s.Path.ToString())); // "missing" was not applied — baseline did not move
    }

    [Fact]
    public async Task ApplyAsync_BackfillsEvidence_LegacyRowsHealOneTime()
    {
        // Legacy baseline rows without evidence (pre-fix) are backfilled from a scan when the size matches;
        // existing evidence is never overwritten, a mismatched size is left alone, and the scan seeds no new rows.
        var state = new InMemorySyncStateRepository();
        var pairId = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
        await state.ReplaceBaselineAsync(pairId,
            [
                new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 5, t),
                new FileSnapshot(RelativePath.Parse("b.txt"), EntryKind.File, 5, t),
                new FileSnapshot(RelativePath.Parse("c.txt"), EntryKind.File, 5, t, ETag: "OLD"),
            ],
            CancellationToken.None);

        await new BaselineUpdater(state, new StashPolicy(new StashPolicyOptions())).ApplyAsync(
            pairId,
            [],
            remoteScanned:
            [
                new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 5, t, ETag: "E1"),
                new FileSnapshot(RelativePath.Parse("b.txt"), EntryKind.File, 6, t, ETag: "E2"),
                new FileSnapshot(RelativePath.Parse("c.txt"), EntryKind.File, 5, t, ETag: "NEW"),
                new FileSnapshot(RelativePath.Parse("d.txt"), EntryKind.File, 5, t, ETag: "E4"),
            ],
            localScanned:
            [
                new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 5, t, ContentHash: "H1"),
                new FileSnapshot(RelativePath.Parse("b.txt"), EntryKind.File, 6, t, ContentHash: "H2"),
            ],
            ct: CancellationToken.None);

        var rows = (await state.LoadBaselineAsync(pairId, CancellationToken.None))
            .ToDictionary(static s => s.Path.ToString(), static s => s);
        Assert.Equal("E1", rows["a.txt"].ETag); // from the left: ETag from the remote scan
        Assert.Equal("H1", rows["a.txt"].ContentHash); // from the left: local hash evidence from the hasher
        Assert.Null(rows["b.txt"].ETag); // sizes differ — not our evidence
        Assert.Null(rows["b.txt"].ContentHash);
        Assert.Equal("OLD", rows["c.txt"].ETag); // existing evidence is sacred
        Assert.False(rows.ContainsKey("d.txt")); // the scan seeds no new rows — only applied operations do 
    }

    /// <summary>
    /// Snapshot constructor for table asserts.
    /// </summary>
    private static FileSnapshot Snap(string path, string content, string? etag = null) =>
        new(RelativePath.Parse(path), EntryKind.File, content.Length, null, etag);

    /// <summary>
    /// Dictionary index built from snapshots.
    /// </summary>
    private static IReadOnlyDictionary<RelativePath, FileSnapshot> Index(params FileSnapshot[] snapshots) =>
        snapshots.ToDictionary(static s => s.Path, static s => s);

    /// <summary>
    /// Operation by kind/path/"after" snapshot.
    /// </summary>
    private static SyncOperation Op(OperationKind kind, string path, FileSnapshot? after) =>
        new(kind, RelativePath.Parse(path), null, after);
}
