using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using FileFerry.Core.Sync.Execution;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// The <see cref="SyncRun"/> orchestrator: a race of two runs per pair, the keyed-lock scoped
/// to one pair only, availability gates, resume priority over planning, cancellation with a
/// journal kept for resume, and a run-log for every finished run.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SyncRunTests
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
    /// Base environment: one pair with in-memory sides and a "live" server.
    /// </summary>
    public SyncRunTests() => _scopes.Add(new SyncRunScope(_pair, _local, _remote, _probe));

    [Fact]
    public async Task Race_TwoConcurrentRunsOnSamePair_SecondRefusedWithoutSideEffects()
    {
        await SeedLocalAsync("a.txt", "A");
        var gate = new GatedFileSystem(_local);
        _scopes.Add(new SyncRunScope(_pair, gate, _remote, _probe));
        var run = Engine();

        var first = run.RunAsync(_pair.Id, CancellationToken.None);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await run.RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorKind.Conflict, second.Error.Kind); // invariant: one run per pair
        Assert.Equal(1, _scopes.OpenCount); // the other trigger never even reached opening the environment

        gate.Release();
        var winner = await first;
        Assert.True(winner.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, winner.Value.Outcome);
    }

    [Fact]
    public async Task Race_DifferentPairs_AreNotSerialized()
    {
        var pairB = new FolderPair(Guid.NewGuid(), _pair.AccountId, "syncb", "tree-b", "Pair B");
        var gateA = new GatedFileSystem(_local);
        var gateB = new GatedFileSystem(new InMemoryFileSystem(_clock));
        _scopes.Add(new SyncRunScope(_pair, gateA, _remote, _probe));
        _scopes.Add(new SyncRunScope(pairB, gateB, new InMemoryRemoteStore(_clock), new FixedProbe()));
        var run = Engine();

        var a = run.RunAsync(_pair.Id, CancellationToken.None);
        var b = run.RunAsync(pairB.Id, CancellationToken.None);

        // both runs are inside the scan simultaneously → the lock is keyed by pair, not global
        await Task.WhenAll(gateA.Entered, gateB.Entered).WaitAsync(TimeSpan.FromSeconds(5));
        gateA.Release();
        gateB.Release();

        Assert.Equal(SyncRunOutcome.Success, (await a).Value.Outcome);
        Assert.Equal(SyncRunOutcome.Success, (await b).Value.Outcome);
    }

    [Fact]
    public async Task TwoWayRun_ConvergesBothSides_BaselineMoves_RunLogSuccess()
    {
        await SeedLocalAsync("docs/a.txt", "A");
        await SeedRemoteAsync("b.txt", "B");

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        Assert.True(result.Value.Planned >= 3); // mkcol docs + upload a + download b
        Assert.Contains("a.txt", await NamesOnRemoteAsync("sync/docs"));
        Assert.Contains("b.txt", await LocalNamesAsync());
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));

        var baseline = await _state.LoadBaselineAsync(_pair.Id, CancellationToken.None);
        Assert.Contains("docs/a.txt", baseline.Select(static s => s.Path.ToString()));
        Assert.Contains("b.txt", baseline.Select(static s => s.Path.ToString()));

        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        Assert.Equal(SyncRunOutcome.Success, log.Outcome);
        Assert.NotNull(log.FinishedAt);
        // the event is structured — the plan is recognized by kind, not by rendered text
        Assert.Contains(log.Events, static e => e.Kind == EventKind.PlanSummary);
    }

    [Fact]
    public async Task ServerUnavailable_FailsFast_FailedLog_NoJournal()
    {
        await SeedLocalAsync("a.txt", "A");
        _probe.Response = Result<ServerStatus, SyncError>.Failure(
            SyncError.Of(ErrorKind.ServerGone, "status.php did not respond"));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.ServerGone, result.Error.Kind);
        Assert.Equal(1, _probe.ProbeCount);
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
        Assert.True((await _remote.ListAsync(RemotePath.Parse("sync"), CancellationToken.None)).IsFailure); // the server was not touched
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        Assert.Equal(SyncRunOutcome.Failed, log.Outcome);
    }

    [Fact]
    public async Task MaintenanceMode_DefersRun_WithoutScanning()
    {
        _probe.Response = Result<ServerStatus, SyncError>.Success(new ServerStatus("30.0.0", MaintenanceMode: true));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.ServerGone, result.Error.Kind);
        Assert.Equal(1, _scopes.OpenCount); // the environment was opened, the scan was not (server in maintenance)
    }

    [Fact]
    public async Task PausedPair_ScheduledRefused_ManualRunsAnyway()
    {
        var paused = _pair with { Paused = true };
        _scopes.Add(new SyncRunScope(paused, _local, _remote, _probe));

        var scheduled = await Engine().RunAsync(paused.Id, CancellationToken.None);
        Assert.True(scheduled.IsFailure);
        Assert.Equal(ErrorKind.Conflict, scheduled.Error.Kind);
        Assert.Equal(0, _probe.ProbeCount); // the pause is checked before the probe

        var manual = await Engine().RunAsync(paused.Id, CancellationToken.None, ignorePaused: true);
        Assert.True(manual.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, manual.Value.Outcome);
    }

    [Fact]
    public async Task PendingJournal_ResumesBeforeAnyPlanning()
    {
        await SeedLocalAsync("a.txt", "A");
        var op = new SyncOperation(
            OperationKind.UploadToRemote, RelativePath.Parse("a.txt"), null,
            new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 1));
        await _state.AppendJournalAsync(
            _pair.Id, new JournalEntry(1, op, JournalState.Planned, _clock.UtcNow), CancellationToken.None);

        var gate = new GatedFileSystem(_local);
        gate.Release(); // had a scan happened, it would pass through; the check is that there was NO scan
        _scopes.Add(new SyncRunScope(_pair, gate, _remote, _probe));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        Assert.Equal(0, gate.ScanCount); // resume does not scan: the journal replays to the end before any scan
        Assert.Contains("a.txt", await NamesOnRemoteAsync("sync"));
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledRun_KeepsJournal_AndNextRunResumesCleanly()
    {
        await SeedLocalAsync("a.txt", "A");
        var hang = new HangingRemoteStore(_remote);
        _scopes.Add(new SyncRunScope(_pair, _local, hang, _probe));
        var run = Engine();

        using var cts = new CancellationTokenSource();
        var first = run.RunAsync(_pair.Id, cts.Token);
        await hang.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var cancelled = await first;
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(SyncRunOutcome.Cancelled, cancelled.Value.Outcome);
        Assert.NotEmpty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None)); // the write-ahead journal survived

        var second = await run.RunAsync(_pair.Id, CancellationToken.None);
        Assert.Equal(SyncRunOutcome.Success, second.Value.Outcome);
        Assert.Contains("a.txt", await NamesOnRemoteAsync("sync"));
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));

        var logs = await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None);
        Assert.Contains(logs, static l => l.Outcome == SyncRunOutcome.Cancelled);
        Assert.Contains(logs, static l => l.Outcome == SyncRunOutcome.Success);
    }

    [Fact]
    public async Task ScopeOpenFailure_FailedRunLog_AndNotFound()
    {
        var missing = Guid.NewGuid();

        var result = await Engine().RunAsync(missing, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        var log = (await _state.RecentRunsAsync(missing, 5, CancellationToken.None)).Single();
        Assert.Equal(SyncRunOutcome.Failed, log.Outcome);
    }

    [Fact]
    public async Task JournalStorageFails_FailureStage_StageErrorStillDecidesTheRun()
    {
        // the run-log is presentation data: storage that rejects the failure record must not
        // replace the failure — the caller and the scheduler act on the stage error
        await SeedLocalAsync("a.txt", "A");
        _probe.Response = Result<ServerStatus, SyncError>.Failure(
            SyncError.Of(ErrorKind.ServerGone, "status.php did not respond"));

        var result = await Engine(state: new UnjournalingState(_state)).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.ServerGone, result.Error.Kind);
    }

    [Fact]
    public async Task JournalStorageFails_CompletedRun_StillReportsSuccess()
    {
        await SeedLocalAsync("docs/a.txt", "A");

        var result = await Engine(state: new UnjournalingState(_state)).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        Assert.Contains("a.txt", await NamesOnRemoteAsync("sync/docs")); // the sync itself happened
    }

    [Fact]
    public async Task PermanentStepFailure_SeedsAppliedToBaseline_NextRunReplansOnlyRemainder()
    {
        await SeedLocalAsync("docs/z.txt", "Z");
        _scopes.Add(new SyncRunScope(_pair, _local, new PoisonedRemoteStore(_remote), _probe));

        var first = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(SyncRunOutcome.Partial, first.Value.Outcome); // mkcol docs passed, the write — a permanent Conflict
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None)); // quarantine instead of a poisoned resume
        var baseline = await _state.LoadBaselineAsync(_pair.Id, CancellationToken.None);
        Assert.Contains("docs", baseline.Select(static s => s.Path.ToString())); // applied items seed even on a broken run

        _scopes.Add(new SyncRunScope(_pair, _local, _remote, _probe)); // the "poison" is gone — the plan rebuilds from state
        var second = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.Equal(SyncRunOutcome.Success, second.Value.Outcome);
        Assert.Equal(1, second.Value.Planned); // only z.txt: the directory is already in the baseline — no churn
        Assert.Contains("z.txt", await NamesOnRemoteAsync("sync/docs"));
    }

    [Fact]
    public async Task CancelDuringQuarantine_AppliedSetStillProjectsIntoBaseline()
    {
        // the user's "Cancel" lands between the quarantine (which wipes the resume evidence) and the
        // baseline projection: the applied step already happened on disk — losing its projection here
        // would make the next run replan a "ghost" out of an empty baseline
        await SeedLocalAsync("docs/z.txt", "Z");
        _scopes.Add(new SyncRunScope(_pair, _local, new PoisonedRemoteStore(_remote), _probe));
        using var stop = new CancellationTokenSource();

        var result = await Engine(state: new CancelDuringQuarantineState(_state, stop)).RunAsync(_pair.Id, stop.Token);

        Assert.True(result.IsSuccess);
        var baseline = await _state.LoadBaselineAsync(_pair.Id, CancellationToken.None);
        Assert.Contains("docs", baseline.Select(static s => s.Path.ToString())); // the projection completed despite the cancellation
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedPipelineCrash_ReportsUnknown_FailedLogSurvives()
    {
        // an unclassified exception must not leave as a crash: the journal has to tell what killed
        // the run, and the scheduler has to see a plain failure instead of a dead process
        _scopes.Add(new SyncRunScope(_pair, new ExplodingFileSystem(_local), _remote, _probe));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Unknown, result.Error.Kind);
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        Assert.Equal(SyncRunOutcome.Failed, log.Outcome);
        Assert.Contains(log.Events, static e => e.Kind == EventKind.RunFailed);
    }

    [Fact]
    public async Task FolderDeletedLocally_WhileServerTreeMoved_ResurrectsWithoutQuarantineLoop()
    {
        // the reviewed regression: a folder disappears locally while the server's collection moves
        // under it. The old rule planned a download of the collection — the step died, quarantine
        // wiped the journal, and every re-run choked on the same step forever. The container is
        // recreated now; the children carry the content by their own rules.
        await SeedLocalAsync("photos/a.txt", "A");
        var first = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.Equal(SyncRunOutcome.Success, first.Value.Outcome);

        await _local.DeleteAsync(RelativePath.Parse("photos"), CancellationToken.None);
        await SeedRemoteAsync("photos/new.txt", "N"); // the collection's evidence moved with it

        var second = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, second.Value.Outcome); // no dead step, no quarantine
        var local = await _local.SnapshotAsync(CancellationToken.None);
        Assert.Contains("photos/new.txt", local.Value.Select(static s => s.Path.ToString()));
        Assert.Empty(await _state.LoadJournalAsync(_pair.Id, CancellationToken.None));

        var third = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.Equal(0, third.Value.Planned); // the state truly converged — nothing waits for the next run
    }

    [Fact]
    public async Task InterruptedWriteFragments_NeverUpload_TheSweepClearsThem()
    {
        // litter of a write the process never finished: the fragment must neither climb to the
        // server nor stay on the device forever; a name that only looks similar is user data
        await SeedLocalAsync("notes.txt", "N");
        await SeedLocalAsync("notes.txt.tmp-0123456789abcdef0123456789abcdef", "fragment");
        await SeedLocalAsync("legacy.tmp-hello", "user content keeps its name");

        var run = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(run.IsSuccess);
        Assert.Equal(SyncRunOutcome.Success, run.Value.Outcome);
        var local = await _local.SnapshotAsync(CancellationToken.None);
        var localPaths = local.Value.Select(static s => s.Path.ToString()).ToList();
        Assert.DoesNotContain("notes.txt.tmp-0123456789abcdef0123456789abcdef", localPaths); // swept off the disk
        Assert.Contains("legacy.tmp-hello", localPaths);
        var remote = await _remote.ListAsync(RemotePath.Parse("sync"), CancellationToken.None);
        var remotePaths = remote.Value.Select(static e => e.Path.ToString()).ToList();
        Assert.Contains("sync/notes.txt", remotePaths);
        Assert.Contains("sync/legacy.tmp-hello", remotePaths);
        Assert.DoesNotContain("sync/notes.txt.tmp-0123456789abcdef0123456789abcdef", remotePaths); // never uploaded
    }

    private SyncRun Engine(IEnumerable<IOperationHandler>? handlers = null, ISyncStateRepository? state = null) =>
        new(
            _scopes,
            new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash),
            state ?? _state,
            _stash,
            _clock,
            handlers);

    private async Task<IReadOnlyList<string>> LocalNamesAsync() =>
        (await _local.SnapshotAsync(CancellationToken.None)).Value
            .Select(static s => s.Path.ToString()).ToArray();

    private async Task SeedLocalAsync(string path, string content) =>
        await _local.WriteAsync(
            RelativePath.Parse(path), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);

    private async Task SeedRemoteAsync(string name, string content) =>
        await _remote.WriteAsync(
            RemotePath.Parse("sync").Combine(name), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);

    private async Task<IReadOnlyList<string>> NamesOnRemoteAsync(string folder) =>
        (await _remote.ListAsync(RemotePath.Parse(folder), CancellationToken.None)).Value
            .Select(static e => e.Path.Name).ToArray();

    /// <summary>
    /// Local side with gates: an "entered the scan" signal + waiting for release — an "active run" trap.
    /// </summary>
    private sealed class GatedFileSystem(ILocalFileSystem inner) : ILocalFileSystem
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _scanCount;

        /// <summary>
        /// The scan has started.
        /// </summary>
        public Task Entered => _entered.Task;

        /// <summary>
        /// How many times the scan ran (the resume path must yield 0).
        /// </summary>
        public int ScanCount => Volatile.Read(ref _scanCount);

        /// <summary>
        /// Let the scan proceed.
        /// </summary>
        public void Release() => _release.TrySetResult();

        public async Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _scanCount);
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

        public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// Remote side: the first write hangs until cancellation, afterwards everything passes. Simulates an interrupted run.
    /// </summary>
    private sealed class HangingRemoteStore(IWritableRemoteStore inner) : IWritableRemoteStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hang = 1;

        /// <summary>
        /// The write has started — cancellation may proceed.
        /// </summary>
        public Task Entered => _entered.Task;

        public async Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _hang, 0) == 1)
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // only the ct releases it
            }

            return await inner.WriteAsync(file, content, ct).ConfigureAwait(false);
        }

        public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct) =>
            inner.ListAsync(folder, ct);

        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) => inner.ReadAsync(file, ct);

        public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct) =>
            inner.CreateFolderAsync(folder, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// Remote where every file write yields a permanent Conflict — like an SAF refusal to copy a directory.
    /// </summary>
    private sealed class PoisonedRemoteStore(IWritableRemoteStore inner) : IWritableRemoteStore
    {
        public Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct) =>
            Task.FromResult(Result<RemoteEntry, SyncError>.Failure(
                SyncError.Of(ErrorKind.Conflict, "Poisoned plan: write impossible")));

        public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct) =>
            inner.ListAsync(folder, ct);

        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) => inner.ReadAsync(file, ct);

        public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct) =>
            inner.CreateFolderAsync(folder, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// State whose run-log write always fails — the shape storage takes when the journal row is
    /// gone from under the run (disk full, a parent row that no longer exists). Everything else
    /// passes through: only the log write dies.
    /// </summary>
    private sealed class UnjournalingState(ISyncStateRepository inner) : ISyncStateRepository
    {
        public Task SaveRunLogAsync(SyncRunLog runLog, CancellationToken ct) =>
            throw new IOException("the journal storage is gone");

        public Task<IReadOnlyList<FileSnapshot>> LoadBaselineAsync(Guid pairId, CancellationToken ct) =>
            inner.LoadBaselineAsync(pairId, ct);

        public Task ReplaceBaselineAsync(Guid pairId, IReadOnlyList<FileSnapshot> baseline, CancellationToken ct) =>
            inner.ReplaceBaselineAsync(pairId, baseline, ct);

        public Task AppendJournalAsync(Guid pairId, JournalEntry entry, CancellationToken ct) =>
            inner.AppendJournalAsync(pairId, entry, ct);

        public Task<IReadOnlyList<JournalEntry>> LoadJournalAsync(Guid pairId, CancellationToken ct) =>
            inner.LoadJournalAsync(pairId, ct);

        public Task ClearJournalAsync(Guid pairId, CancellationToken ct) => inner.ClearJournalAsync(pairId, ct);

        public Task<IReadOnlyList<SyncRunLog>> RecentRunsAsync(Guid pairId, int limit, CancellationToken ct) =>
            inner.RecentRunsAsync(pairId, limit, ct);
    }

    /// <summary>
    /// State that delivers the user's cancellation at the quarantine moment and, like real storage,
    /// rejects a baseline write under a cancelled token: the applied-set projection survives only
    /// when the run performs it on its own, not on the run's token.
    /// </summary>
    private sealed class CancelDuringQuarantineState(ISyncStateRepository inner, CancellationTokenSource stop) : ISyncStateRepository
    {
        public Task ClearJournalAsync(Guid pairId, CancellationToken ct)
        {
            stop.Cancel(); // "Cancel" arrives exactly between the step failure and the projection
            return inner.ClearJournalAsync(pairId, ct);
        }

        public Task ReplaceBaselineAsync(Guid pairId, IReadOnlyList<FileSnapshot> baseline, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return inner.ReplaceBaselineAsync(pairId, baseline, ct);
        }

        public Task<IReadOnlyList<FileSnapshot>> LoadBaselineAsync(Guid pairId, CancellationToken ct) =>
            inner.LoadBaselineAsync(pairId, ct);

        public Task AppendJournalAsync(Guid pairId, JournalEntry entry, CancellationToken ct) =>
            inner.AppendJournalAsync(pairId, entry, ct);

        public Task<IReadOnlyList<JournalEntry>> LoadJournalAsync(Guid pairId, CancellationToken ct) =>
            inner.LoadJournalAsync(pairId, ct);

        public Task SaveRunLogAsync(SyncRunLog runLog, CancellationToken ct) => inner.SaveRunLogAsync(runLog, ct);

        public Task<IReadOnlyList<SyncRunLog>> RecentRunsAsync(Guid pairId, int limit, CancellationToken ct) =>
            inner.RecentRunsAsync(pairId, limit, ct);
    }

    /// <summary>
    /// Local side throwing something the pipeline never expects — the shape of a bug: an
    /// unclassified provider error or a parser blowing up on a rotten row.
    /// </summary>
    private sealed class ExplodingFileSystem(ILocalFileSystem inner) : ILocalFileSystem
    {
        public Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct) =>
            throw new InvalidOperationException("simulated unclassified crash");

        public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) => inner.ReadAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
            inner.WriteAsync(path, content, ct);

        public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
            inner.CreateFolderAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.CopyAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }
}
