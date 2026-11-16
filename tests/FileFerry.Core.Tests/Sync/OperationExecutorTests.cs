using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using FileFerry.Core.Sync.Execution;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Mechanics of <see cref="OperationExecutor"/>: the write-ahead journal, resume of an interrupted
/// run without duplicates, and the "delete only through the stash" guard (invariant).
/// </summary>
[Trait("Category", "Unit")]
public sealed class OperationExecutorTests
{
    private readonly FolderPair _pair = new(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
    private readonly MutableClock _clock = new();
    private readonly InMemoryFileSystem _local = new();
    private readonly InMemoryRemoteStore _remote = new();
    private readonly InMemorySyncStateRepository _state = new();
    private readonly StashPolicy _stash = new(new StashPolicyOptions());

    [Fact]
    public async Task Execute_AppliesWholePlan_AndClearsJournal()
    {
        await SeedLocalAsync("docs/a.txt", "A");
        await SeedRemoteAsync("old.txt", "O");
        var plan = new List<SyncOperation>
        {
            new(OperationKind.CreateRemoteFolder, RelativePath.Parse("docs")),
            new(OperationKind.UploadToRemote, RelativePath.Parse("docs/a.txt")),
            new(OperationKind.MoveRemote, RelativePath.Parse("new.txt"), RelativePath.Parse("old.txt")),
        };

        var report = await Executor().ExecuteAsync(Ctx(), plan, CancellationToken.None);

        Assert.True(report.Completed);
        Assert.Equal(3, report.Applied.Count);
        Assert.Contains("new.txt", (await RemoteListAsync("sync")).Select(static e => e.Path.Name));
        Assert.DoesNotContain("old.txt", (await RemoteListAsync("sync")).Select(static e => e.Path.Name));
        Assert.Contains("a.txt", (await RemoteListAsync("sync/docs")).Select(static e => e.Path.Name));
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    [Fact]
    public async Task PermanentStepFailure_QuarantinesJournal_SoNextRunReplansAnew()
    {
        var plan = new List<SyncOperation>
        {
            new(OperationKind.UploadToRemote, RelativePath.Parse("missing.txt")),
            new(OperationKind.UploadToRemote, RelativePath.Parse("second.txt")),
        };

        var report = await Executor().ExecuteAsync(Ctx(), plan, CancellationToken.None);

        Assert.False(report.Completed);
        Assert.Equal(ErrorKind.NotFound, report.Error!.Kind);
        Assert.Same(plan[0], report.FailedOperation);
        Assert.Empty(report.Applied);

        // There was a write-ahead journal, but a step error = the plan contradicts the sides:
        // the journal is quarantined — replaying the poison becomes impossible.
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));

        // The next run does not hear "ResumeAsync required" — it replans from scratch based on the state.
        var again = await Executor().ExecuteAsync(Ctx(), [plan[1]], CancellationToken.None);
        Assert.Equal(ErrorKind.NotFound, again.Error!.Kind);
    }

    [Fact]
    public async Task CrashLeavesUnfinishedJournal_ResumeReplaysRemainderWithoutDuplicates()
    {
        await SeedLocalAsync("b.txt", "B");
        var stashPath = _stash.StashDestination(RelativePath.Parse("b.txt"), _clock.UtcNow);
        var copy = new SyncOperation(OperationKind.CopyLocal, stashPath, RelativePath.Parse("b.txt"));
        var del = new SyncOperation(OperationKind.DeleteLocal, RelativePath.Parse("b.txt"));

        // "kill -9" after an applied copy: a side is already changed; journal — Applied+Planned with no Failed at all.
        // For exactly this window the journal survives the break — unlike a step error, which goes to quarantine.
        Assert.True((await _local.CopyAsync(RelativePath.Parse("b.txt"), stashPath, CancellationToken.None)).IsSuccess);
        await _state.AppendJournalAsync(_pair.Id, new JournalEntry(1, copy, JournalState.Applied, _clock.UtcNow), CancellationToken.None);
        await _state.AppendJournalAsync(_pair.Id, new JournalEntry(2, del, JournalState.Planned, _clock.UtcNow), CancellationToken.None);

        var resumed = await Executor().ResumeAsync(Ctx(), CancellationToken.None);

        Assert.True(resumed.Completed);
        Assert.DoesNotContain(LocalSnapshot(), static s => s.Path.ToString() == "b.txt");
        Assert.Single(LocalSnapshot(), static s => !s.IsDirectory && s.Path.StartsWith(RelativePath.Parse("FileFerry Trash"))); // exactly one copy file — no duplicates
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_RefusesDeleteWithoutStashCopy()
    {
        await SeedRemoteAsync("c.txt", "C");
        var plan = new List<SyncOperation> { new(OperationKind.DeleteRemote, RelativePath.Parse("c.txt")) };

        var report = await Executor().ExecuteAsync(Ctx(), plan, CancellationToken.None);

        Assert.False(report.Completed);
        Assert.Equal(ErrorKind.Conflict, report.Error!.Kind);
        Assert.Contains("c.txt", (await RemoteListAsync("sync")).Select(static e => e.Path.Name)); // server untouched
        // A guard trip = a self-contradictory plan ⟹ quarantine as well: such a journal would replay forever.
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DirectoryDeleteWithoutOwnCopy_PassesGuard_ChildrenCoverTheContent()
    {
        // Live run 561: the guard demanded a copy of the directory itself too, which the planner
        // deliberately never copies — the content is protected by the children's copies (each child is a separate change).
        await SeedLocalAsync("bulk/a.txt", "A");
        var bulk = RelativePath.Parse("bulk");
        var doomedDir = new FileSnapshot(bulk, EntryKind.Directory, 0);
        var stashChild = _stash.StashDestination(RelativePath.Parse("bulk/a.txt"), _clock.UtcNow);
        var plan = new List<SyncOperation>
        {
            new(OperationKind.CopyLocal, stashChild, RelativePath.Parse("bulk/a.txt")),
            new(OperationKind.DeleteLocal, RelativePath.Parse("bulk/a.txt")),
            new(OperationKind.DeleteLocal, bulk, null, doomedDir), // no copy of its own — valid
        };

        var report = await Executor().ExecuteAsync(Ctx(), plan, CancellationToken.None);

        Assert.True(report.Completed);
        Assert.DoesNotContain(LocalSnapshot(), static s => s.Path.ToString() == "bulk");
    }

    [Fact]
    public async Task Resume_SkipsApplied_ReplaysMoveIdempotently()
    {
        await SeedRemoteAsync("new.txt", "O"); // the move already happened before the break
        var move = new SyncOperation(OperationKind.MoveRemote, RelativePath.Parse("new.txt"), RelativePath.Parse("old.txt"));
        await _state.AppendJournalAsync(_pair.Id, new JournalEntry(1, move, JournalState.Planned, _clock.UtcNow), CancellationToken.None);

        var report = await Executor().ResumeAsync(Ctx(), CancellationToken.None);

        Assert.True(report.Completed);
        Assert.Single(report.Applied);
        Assert.Contains("new.txt", (await RemoteListAsync("sync")).Select(static e => e.Path.Name));
    }

    [Fact]
    public async Task Execute_WithPendingJournal_RefusesUntilResume()
    {
        var op = new SyncOperation(OperationKind.CreateLocalFolder, RelativePath.Parse("x"));
        await _state.AppendJournalAsync(_pair.Id, new JournalEntry(1, op, JournalState.Planned, _clock.UtcNow), CancellationToken.None);

        var report = await Executor().ExecuteAsync(Ctx(), [op], CancellationToken.None);

        Assert.False(report.Completed);
        Assert.Equal(ErrorKind.Conflict, report.Error!.Kind);
        Assert.NotEmpty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RecordConvergence_AppliesAsNoOp_CarriesEvidenceIntoJournal()
    {
        // A no-op operation — zero touch of either side, but the evidence passes through the journal
        // and later seeds a baseline row (the BaselineUpdater projection is covered in the run tests).
        await SeedRemoteAsync("keep.txt", "K"); // reference: the server's composition must not change
        var after = new FileSnapshot(
            RelativePath.Parse("conv.txt"), EntryKind.File, 4, _clock.UtcNow, ETag: "e9", ContentHash: "h9");
        var plan = new List<SyncOperation> { new(OperationKind.RecordConvergence, RelativePath.Parse("conv.txt"), null, after) };

        var report = await Executor().ExecuteAsync(Ctx(), plan, CancellationToken.None);

        Assert.True(report.Completed);
        var applied = Assert.Single(report.Applied);
        Assert.Equal("e9", applied.After!.ETag);
        Assert.Equal("h9", applied.After!.ContentHash);
        Assert.DoesNotContain(LocalSnapshot(), static s => s.Path.ToString() == "conv.txt"); // no side effect at all
        Assert.Equal(["keep.txt"], (await RemoteListAsync("sync")).Select(static e => e.Path.Name)); // server untouched
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    /// <summary>
    /// Assembles an executor over the shared fakes (a test "kill" is injected via the local argument).
    /// </summary>
    /// <param name="local">Local side; defaults to the base in-memory one.</param>
    private OperationExecutor Executor(ILocalFileSystem? local = null) =>
        new(local ?? _local, _remote, _state, _stash, OperationExecutor.DefaultHandlers());

    /// <summary>
    /// Minimal run context (the executor needs only the pair and the moment in time).
    /// </summary>
    private SyncRunContext Ctx() =>
        new(_pair, _clock.UtcNow,
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>());

    private async Task SeedLocalAsync(string path, string content)
    {
        var written = await _local.WriteAsync(RelativePath.Parse(path), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);
        Assert.True(written.IsSuccess);
    }

    /// <summary>
    /// Write a file into the remote store under the pair root.
    /// </summary>
    private async Task SeedRemoteAsync(string name, string content)
    {
        var written = await _remote.WriteAsync(RemotePath.Parse("sync").Combine(name), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);
        Assert.True(written.IsSuccess);
    }

    /// <summary>
    /// Local FS tree (assert-ready).
    /// </summary>
    private IReadOnlyList<FileSnapshot> LocalSnapshot()
    {
        var snapshot = _local.SnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(snapshot.IsSuccess);
        return snapshot.Value;
    }

    /// <summary>
    /// Child entries of a remote collection (assert-ready).
    /// </summary>
    private async Task<IReadOnlyList<RemoteEntry>> RemoteListAsync(string folder)
    {
        var listing = await _remote.ListAsync(RemotePath.Parse(folder), CancellationToken.None);
        Assert.True(listing.IsSuccess);
        return listing.Value;
    }
}
