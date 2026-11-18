using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Keep-side resolution of a KeepBoth conflict. After the conflict run the main path carries the
/// local version on both sides and every copy pair "name (conflicted copy stamp).ext" carries the
/// foreign version. Resolution: "keep local" discards the copies (both sides, stash first);
/// "keep server" promotes the newest copy's content onto the main path on both sides and discards
/// all copies. Every disappearance goes through the stash buffer; the baseline moves only by
/// applied steps; the next plain run must plan zero operations (state truly converged).
/// </summary>
[Trait("Category", "SyncScenario")]
public sealed class ConflictResolutionRunTests
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

    public ConflictResolutionRunTests()
    {
        _local = new InMemoryFileSystem(_clock);
        _remote = new InMemoryRemoteStore(_clock);
        _scopes.Add(new SyncRunScope(_pair, _local, _remote, _probe));
    }

    [Fact]
    public async Task KeepLocal_DiscardsEveryConflictCopyOnBothSides_MainStays_NextRunPlansZero()
    {
        await CreateConflictAsync("LOCAL-A", "REMOTE-B");
        Assert.NotEmpty(CopyNames(await LocalNamesAsync()));
        Assert.NotEmpty(CopyNames(await RemoteNamesAsync()));

        var result = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Local, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Empty(CopyNames(await LocalNamesAsync()));
        Assert.Empty(CopyNames(await RemoteNamesAsync()));
        Assert.Equal("LOCAL-A", await ReadLocalAsync("a.txt"));
        Assert.Equal("LOCAL-A", await ReadRemoteAsync("a.txt"));
        // the discarded server version is recoverable — the buffer holds its copy until the TTL ends
        Assert.Contains(await StashNamesAsync(), static n => n.Contains("conflicted copy", StringComparison.Ordinal));
        // baseline converged: the copy rows are gone, the main row lives
        var baseline = await _state.LoadBaselineAsync(_pair.Id, CancellationToken.None);
        Assert.Contains(baseline, static s => s.Path.Name == "a.txt" && !s.IsDirectory);
        Assert.DoesNotContain(baseline, static s => s.Path.Name.Contains("conflicted copy", StringComparison.Ordinal));

        var followUp = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.True(followUp.IsSuccess);
        Assert.Equal(0, followUp.Value.Planned); // nothing to re-shuffle — the resolution really converged
    }

    [Fact]
    public async Task KeepServer_PromotesTheServerVersionOnBothSides_NextRunPlansZero()
    {
        await CreateConflictAsync("LOCAL-A", "REMOTE-B");
        // the resolution happens later than the conflict: the copy's real mtime is a new moment,
        // and the baseline must carry that moment, not the moment planned (a stale row would make
        // the next run see a phantom local change and churn a re-upload)
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Server, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        Assert.Equal("REMOTE-B", await ReadLocalAsync("a.txt"));
        Assert.Equal("REMOTE-B", await ReadRemoteAsync("a.txt"));
        Assert.Empty(CopyNames(await LocalNamesAsync()));
        Assert.Empty(CopyNames(await RemoteNamesAsync()));
        var baseline = await _state.LoadBaselineAsync(_pair.Id, CancellationToken.None);
        Assert.DoesNotContain(baseline, static s => s.Path.Name.Contains("conflicted copy", StringComparison.Ordinal));

        var followUp = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.Equal(0, followUp.Value.Planned);
    }

    [Fact]
    public async Task KeepServer_WithSeveralCopies_PromotesTheNewestStamp_AndDiscardsAll()
    {
        await CreateConflictAsync("LOCAL-A1", "REMOTE-B1");
        // a second conflict over the same pair of edits, a day later (distinct copy stamp)
        _clock.Advance(TimeSpan.FromDays(1));
        await SeedLocalAsync("a.txt", "LOCAL-A2");
        _clock.Advance(TimeSpan.FromHours(1));
        await SeedRemoteAsync("a.txt", "REMOTE-B2");
        var second = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.Equal(1, second.Value.Conflicts);
        Assert.Equal(2, CopyNames(await LocalNamesAsync()).Count);

        var result = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Server, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal("REMOTE-B2", await ReadLocalAsync("a.txt")); // the newest copy wins
        Assert.Equal("REMOTE-B2", await ReadRemoteAsync("a.txt"));
        Assert.Empty(CopyNames(await LocalNamesAsync()));
        Assert.Empty(CopyNames(await RemoteNamesAsync()));
    }

    [Fact]
    public async Task Resolve_WithoutConflictCopies_FailsNotFound_LeavesStateIntact()
    {
        await SeedLocalAsync("a.txt", "ONLY");
        await Engine().RunAsync(_pair.Id, CancellationToken.None);

        var result = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Server, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        Assert.Equal("ONLY", await ReadLocalAsync("a.txt"));
        Assert.DoesNotContain(await _state.LoadBaselineAsync(_pair.Id, CancellationToken.None),
            static s => s.Path.Name.Contains("conflicted copy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepLocal_WhenMainVanishedLocally_RefusedNotFound()
    {
        await CreateConflictAsync("LOCAL-A", "REMOTE-B");
        await _local.DeleteAsync(RelativePath.Parse("a.txt"), CancellationToken.None);

        var result = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Local, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        // the foreign-version copies survive a refused resolution
        Assert.NotEmpty(CopyNames(await LocalNamesAsync()));
    }

    [Fact]
    public async Task Resolve_WhileAPlainRunIsActive_RefusedByThePairLock()
    {
        await SeedLocalAsync("a.txt", "A");
        var gate = new GatedFileSystem(_local);
        _scopes.Add(new SyncRunScope(_pair, gate, _remote, _probe));
        var engine = Engine();

        var first = engine.RunAsync(_pair.Id, CancellationToken.None);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var resolution = await engine.ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Server, CancellationToken.None);

        Assert.True(resolution.IsFailure);
        Assert.Equal(ErrorKind.Conflict, resolution.Error.Kind);

        gate.Release();
        Assert.True((await first).IsSuccess);
    }

    [Fact]
    public async Task Resolve_CancelledMidWay_ResumesOnTheNextRun_ToTheSameConvergedState()
    {
        await CreateConflictAsync("LOCAL-A", "REMOTE-B");
        var cts = new CancellationTokenSource();
        var flaky = new CancelAfterCopyFileSystem(_local, cts);
        _scopes.Add(new SyncRunScope(_pair, flaky, _remote, _probe));

        var partial = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Server, cts.Token);

        Assert.True(partial.IsSuccess); // a cancelled outcome is not a failure
        Assert.NotEqual(SyncRunOutcome.Success, partial.Value.Outcome);

        // the journal drives the next plain run — resume completes what resolution started
        var resumed = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.True(resumed.IsSuccess);
        Assert.Equal("REMOTE-B", await ReadLocalAsync("a.txt"));
        Assert.Equal("REMOTE-B", await ReadRemoteAsync("a.txt"));
        Assert.Empty(CopyNames(await LocalNamesAsync()));
        Assert.Empty(CopyNames(await RemoteNamesAsync()));
    }

    [Fact]
    public async Task Resolution_LogsAStructuredEvent_ThatRendersThroughThePhraseTable()
    {
        await CreateConflictAsync("LOCAL-A", "REMOTE-B");

        Assert.True((await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("a.txt"), KeepSide.Server, CancellationToken.None)).IsSuccess);

        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).First(l => l.Events.Any(
            static e => e.Kind == EventKind.ConflictResolved));
        var resolved = Assert.Single(log.Events, static e => e.Kind == EventKind.ConflictResolved);
        Assert.Equal("a.txt", resolved.Path!.ToString());
        Assert.Equal("server", resolved.Args!["side"]);
        Assert.Equal("EventResolvedServer", resolved.Phrase().Key);
    }

    // --- environment ---

    /// <summary>
    /// Brings the pair to a BothModified-conflicted state: a.txt with different content on both
    /// sides, one run over it — KeepBoth leaves copies of both versions on both sides.
    /// </summary>
    private async Task CreateConflictAsync(string localContent, string remoteContent)
    {
        await SeedLocalAsync("a.txt", localContent);
        _clock.Advance(TimeSpan.FromHours(1));
        await SeedRemoteAsync("a.txt", remoteContent);
        _clock.Advance(TimeSpan.FromHours(1));
        var conflictRun = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.True(conflictRun.IsSuccess);
        Assert.Equal(1, conflictRun.Value.Conflicts);
    }

    private SyncRun Engine() =>
        new(
            _scopes,
            new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash),
            _state,
            _stash,
            _clock);

    private async Task SeedLocalAsync(string path, string content) =>
        Assert.True((await _local.WriteAsync(
            RelativePath.Parse(path), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None)).IsSuccess);

    private async Task SeedRemoteAsync(string name, string content) =>
        Assert.True((await _remote.WriteAsync(
            RemotePath.Parse("sync").Combine(name), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None)).IsSuccess);

    private async Task<string> ReadLocalAsync(string path)
    {
        var read = await _local.ReadAsync(RelativePath.Parse(path), CancellationToken.None);
        Assert.True(read.IsSuccess);
        using var reader = new StreamReader(read.Value, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task<string> ReadRemoteAsync(string path)
    {
        var read = await _remote.ReadAsync(RemotePath.Parse("sync").Combine(path), CancellationToken.None);
        Assert.True(read.IsSuccess);
        using var reader = new StreamReader(read.Value, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Tree file names, trash buffer excluded — a copy kept there is not a live version any more.
    /// </summary>
    private async Task<IReadOnlyList<string>> LocalNamesAsync() =>
        (await _local.SnapshotAsync(CancellationToken.None)).Value
            .Where(static s => !s.IsDirectory && !s.Path.StartsWith(Trash))
            .Select(static s => s.Path.Name).ToArray();

    /// <summary>
    /// Names of the files currently held in the stash buffer.
    /// </summary>
    private async Task<IReadOnlyList<string>> StashNamesAsync() =>
        (await _local.SnapshotAsync(CancellationToken.None)).Value
            .Where(static s => !s.IsDirectory && s.Path.StartsWith(Trash))
            .Select(static s => s.Path.Name).ToArray();

    private async Task<IReadOnlyList<string>> RemoteNamesAsync() =>
        (await _remote.ListAsync(RemotePath.Parse("sync"), CancellationToken.None)).Value
            .Where(static e => e.Kind != EntryKind.Directory)
            .Select(static e => e.Path.Name).ToArray();

    private static IReadOnlyList<string> CopyNames(IReadOnlyList<string> names) =>
        names.Where(static n => n.Contains("conflicted copy", StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Local side with a scan gate: an "active run" trap for the lock refusal check.
    /// </summary>
    private sealed class GatedFileSystem(ILocalFileSystem inner) : ILocalFileSystem
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await inner.SnapshotAsync(ct).ConfigureAwait(false);
        }

        public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) => inner.ReadAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
            inner.WriteAsync(path, content, ct);

        public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
            inner.CreateFolderAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.CopyAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) =>
            inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// Wrapper: the first local copy cancels the caller's token (the resolution is cut after its
    /// first applied step — the rest of the journal must live on through resume).
    /// </summary>
    private sealed class CancelAfterCopyFileSystem(InMemoryFileSystem inner, CancellationTokenSource cts) : ILocalFileSystem
    {
        private int _copied;

        public Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct) =>
            inner.SnapshotAsync(ct);

        public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) =>
            inner.ReadAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
            inner.WriteAsync(path, content, ct);

        public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
            inner.CreateFolderAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _copied) == 1)
            {
                cts.Cancel();
            }

            return inner.CopyAsync(from, to, ct);
        }

        public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) =>
            inner.DeleteAsync(path, ct);
    }
}
