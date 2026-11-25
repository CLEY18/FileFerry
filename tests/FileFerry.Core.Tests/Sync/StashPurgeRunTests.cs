using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Stash TTL purge — the run stage wired in after operations are applied.
/// An expired copy vanishes from disk and from the index; a live one is untouched; index rows
/// outside the stash root are never deleted (protection against index garbage); a delete
/// failure does not break the run. Determinism via <see cref="IClock"/>.
/// </summary>
[Trait("Category", "SyncScenario")]
public sealed class StashPurgeRunTests
{
    private static readonly RelativePath Trash = RelativePath.Parse("FileFerry Trash");

    private readonly MutableClock _clock = new();
    private readonly StashPolicy _stash = new(new StashPolicyOptions());
    private readonly InMemorySyncStateRepository _state = new();
    private readonly FakeSyncScopeFactory _scopes = new();
    private readonly FixedProbe _probe = new();
    private readonly FolderPair _pair = new(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
    private readonly InMemoryFileSystem _local;
    private readonly InMemoryRemoteStore _remote;
    private readonly InMemoryStashRepository _stashIndex = new();

    /// <summary>
    /// Environment: one pair, both sides in-memory on a virtual clock, server is "live".
    /// </summary>
    public StashPurgeRunTests()
    {
        _local = new InMemoryFileSystem(_clock);
        _remote = new InMemoryRemoteStore(_clock);
        _scopes.Add(new SyncRunScope(_pair, _local, _remote, _probe));
    }

    [Fact]
    public async Task Run_PurgesExpiredCopy_FileAndIndexGone_EventReports()
    {
        await SeedStashCopyAsync("old/a.txt", age: TimeSpan.FromDays(40));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        // the copy vanishes from disk (empty dir markers remain — SAF does not "sweep" them, this is not a loss)…
        Assert.DoesNotContain("FileFerry Trash/old/a.txt",
            (await _local.SnapshotAsync(CancellationToken.None)).Value.Select(static s => s.Path.ToString()));
        Assert.Empty(await _stashIndex.ListAsync(_pair.Id, CancellationToken.None)); // …and from the index
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        var purge = Assert.Single(log.Events, static e => e.Kind == EventKind.StashPurged);
        Assert.Equal("1", purge.Args!["count"]);
    }

    [Fact]
    public async Task Run_KeepsUnexpiredCopy_AndEmitsNoPurgeEvent()
    {
        await SeedStashCopyAsync("fresh/b.txt", age: TimeSpan.FromDays(10));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("FileFerry Trash/fresh/b.txt",
            (await _local.SnapshotAsync(CancellationToken.None)).Value.Select(static s => s.Path.ToString()));
        Assert.Single(await _stashIndex.ListAsync(_pair.Id, CancellationToken.None));
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        Assert.DoesNotContain(log.Events, static e => e.Kind == EventKind.StashPurged);
    }

    [Fact]
    public async Task Purge_NeverTouchesPathsOutsideStashRoot()
    {
        // garbage row: an "expired copy" points at a live pair file outside the buffer
        await SeedLocalAsync("keep-me.txt", "VITAL");
        await _stashIndex.AddAsync(new StashEntry(
            _pair.Id, RelativePath.Parse("keep-me.txt"), RelativePath.Parse("keep-me.txt"),
            _clock.UtcNow - TimeSpan.FromDays(90)), CancellationToken.None);

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("keep-me.txt",
            (await _local.SnapshotAsync(CancellationToken.None)).Value.Select(static s => s.Path.ToString()));
        Assert.Single(await _stashIndex.ListAsync(_pair.Id, CancellationToken.None)); // the row stays
    }

    [Fact]
    public async Task Purge_DeleteFails_KeepsRowAndRunSucceeds()
    {
        var copy = await SeedStashCopyAsync("old/c.txt", age: TimeSpan.FromDays(40));
        var poison = new DeleteFailingFileSystem(_local, copy.Path);
        _scopes.Add(new SyncRunScope(_pair, poison, _remote, _probe));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess); // purge is best-effort: the run does not break
        Assert.Single(await _stashIndex.ListAsync(_pair.Id, CancellationToken.None)); // retry on the next run
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        Assert.DoesNotContain(log.Events, static e => e.Kind == EventKind.StashPurged);
    }

    [Fact]
    public async Task Purge_MissingCopy_HealsIndexRow()
    {
        // the copy was removed from disk outside the app — the index row must not lie to the trash-buffer screen forever
        var copy = await SeedStashCopyAsync("old/d.txt", age: TimeSpan.FromDays(40));
        await _local.DeleteAsync(copy.Path, CancellationToken.None);

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(await _stashIndex.ListAsync(_pair.Id, CancellationToken.None));
    }

    /// <summary>
    /// A fresh copy in the buffer with index age <paramref name="age"/>; returns its snapshot.
    /// </summary>
    private async Task<FileSnapshot> SeedStashCopyAsync(string relative, TimeSpan age)
    {
        var path = Trash.Combine(RelativePath.Parse(relative));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("copy"));
        var written = await _local.WriteAsync(path, stream, CancellationToken.None);
        Assert.True(written.IsSuccess);
        var entry = new StashEntry(_pair.Id, path, RelativePath.Parse(relative), _clock.UtcNow - age);
        await _stashIndex.AddAsync(entry, CancellationToken.None);
        return written.Value;
    }

    private async Task SeedLocalAsync(string path, string content)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        Assert.True((await _local.WriteAsync(RelativePath.Parse(path), stream, CancellationToken.None)).IsSuccess);
    }

    private SyncRun Engine() =>
        new(
            _scopes,
            new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash),
            _state,
            _stash,
            _clock,
            stashIndex: _stashIndex);

    /// <summary>
    /// Wrapper: <see cref="DeleteAsync"/> for one specific path always fails (provider failure).
    /// </summary>
    private sealed class DeleteFailingFileSystem(InMemoryFileSystem inner, RelativePath poison) : ILocalFileSystem
    {
        public Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct) =>
            inner.SnapshotAsync(ct);

        public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) =>
            inner.ReadAsync(path, ct);

        public Task<Result<bool, SyncError>> ExistsAsync(RelativePath path, CancellationToken ct) => inner.ExistsAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
            inner.WriteAsync(path, content, ct);

        public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
            inner.CreateFolderAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.CopyAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) =>
            path == poison
                ? Task.FromResult(Result<Ok, SyncError>.Failure(SyncError.Of(ErrorKind.PermissionLost, "provider said no")))
                : inner.DeleteAsync(path, ct);
    }
}
