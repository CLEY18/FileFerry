using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// The <see cref="SyncRun"/> progress observer: <c>OnPlanned</c> fires exactly once, before the first tick;
/// ticks — one per applied step (successful run: ticks == Planned); the resume path counts
/// already-Applied journal entries so the UI progress bar never starts from zero.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SyncRunProgressTests
{
    private readonly MutableClock _clock = new();
    private readonly StashPolicy _stash = new(new StashPolicyOptions());
    private readonly InMemorySyncStateRepository _state = new();
    private readonly FakeSyncScopeFactory _scopes = new();
    private readonly FixedProbe _probe = new();
    private readonly FolderPair _pair = new(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
    private readonly InMemoryFileSystem _local = new();
    private readonly InMemoryRemoteStore _remote = new();

    /// <summary>
    /// Base environment — the same as in SyncRunTests (in-memory sides, live server).
    /// </summary>
    public SyncRunProgressTests() => _scopes.Add(new SyncRunScope(_pair, _local, _remote, _probe));

    [Fact]
    public async Task FreshRun_PlannedAnnouncedOnceBeforeTicks()
    {
        await SeedLocalAsync("docs/a.txt", "A");
        await SeedRemoteAsync("b.txt", "B");
        var observer = new RecordingObserver();

        var result = await Engine(observer).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { result.Value.Planned }, observer.PlannedCounts); // one OnPlanned, total known
        Assert.Equal(result.Value.Planned, observer.Ticks);                   // "File N of N" reaches the edge
        Assert.Equal("planned", observer.Events[0]);                          // total known before the first tick
    }

    [Fact]
    public async Task ResumedRun_TotalIsJournalSize_AppliedStepsAlsoTick()
    {
        await SeedLocalAsync("a.txt", "A");
        var hang = new HangingRemoteStore(_remote);
        _scopes.Add(new SyncRunScope(_pair, _local, hang, _probe));
        var run = Engine();

        using var cts = new CancellationTokenSource();
        var first = run.RunAsync(_pair.Id, cts.Token);
        await hang.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await first;
        var journal = await _state.LoadJournalAsync(_pair.Id, CancellationToken.None);
        Assert.NotEmpty(journal); // the write-ahead journal survived — resume has something to do

        var observer = new RecordingObserver();
        var second = await Engine(observer).RunAsync(_pair.Id, CancellationToken.None);

        Assert.Equal(SyncRunOutcome.Success, second.Value.Outcome);
        Assert.Equal(new[] { journal.Count }, observer.PlannedCounts); // resume: total = journal size
        Assert.Equal(journal.Count, observer.Ticks);                   // Applied skips move the bar too
    }

    [Fact]
    public async Task EmptyPlan_PlannedZeroAndNoTicks()
    {
        var observer = new RecordingObserver();

        var result = await Engine(observer).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 0 }, observer.PlannedCounts); // the bar is not "indeterminate" — zero is known right away
        Assert.Equal(0, observer.Ticks);
    }

    /// <summary>
    /// Assembles the orchestrator with a progress observer (standard policies).
    /// </summary>
    private SyncRun Engine(ISyncRunObserver? observer = null) =>
        new(
            _scopes,
            new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash),
            _state,
            _stash,
            _clock,
            handlers: null,
            observer: observer);

    private Task SeedLocalAsync(string path, string content) =>
        _local.WriteAsync(
            RelativePath.Parse(path),
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            CancellationToken.None);

    private Task SeedRemoteAsync(string path, string content) =>
        _remote.WriteAsync(
            RemotePath.Parse($"sync/{path}"),
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            CancellationToken.None);

    /// <summary>
    /// Spy for the <see cref="ISyncRunObserver"/> port: call order and call counts.
    /// </summary>
    private sealed class RecordingObserver : ISyncRunObserver
    {
        /// <summary>
        /// Event sequence ("planned" / "tick") — records ordering.
        /// </summary>
        public List<string> Events { get; } = [];

        /// <summary>
        /// Total values received by OnPlanned.
        /// </summary>
        public List<int> PlannedCounts { get; } = [];

        /// <summary>
        /// Count of OnStepApplied calls.
        /// </summary>
        public int Ticks => Events.Count(static e => e == "tick");

        /// <inheritdoc />
        public void OnPlanned(Guid pairId, int totalSteps)
        {
            Events.Add("planned");
            PlannedCounts.Add(totalSteps);
        }

        /// <inheritdoc />
        public void OnStepApplied(Guid pairId) => Events.Add("tick");
    }

    /// <summary>
    /// Remote side that "hangs" on the first write: deterministic cancellation mid-run.
    /// </summary>
    private sealed class HangingRemoteStore(IWritableRemoteStore inner) : IWritableRemoteStore
    {
        /// <summary>
        /// The "entered the write" signal — the test cancels the run at exactly that moment.
        /// </summary>
        public SemaphoreSlim Entered { get; } = new(0);

        private int _hung;

        /// <inheritdoc />
        public async Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _hung, 1) == 0)
            {
                Entered.Release();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }

            return await inner.WriteAsync(file, content, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct) =>
            inner.CreateFolderAsync(folder, ct);

        /// <inheritdoc />
        public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        /// <inheritdoc />
        public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        /// <inheritdoc />
        public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct) =>
            inner.ListAsync(folder, ct);

        /// <inheritdoc />
        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) => inner.ReadAsync(file, ct);
    }
}
