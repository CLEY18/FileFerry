using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// End of pipeline: evidence obtained <b>after</b> a write (fresh server ETag,
/// local write snapshot) must reach the baseline — both on a fresh run and after resume
/// (the journal persists <c>After</c>). A second run on the same pair — zero operations (no-churn).
/// </summary>
[Trait("Category", "SyncScenario")]
public sealed class BaselineEvidenceRunTests
{
    private readonly MutableClock _clock = new();
    private readonly StashPolicy _stash = new(new StashPolicyOptions());
    private readonly InMemorySyncStateRepository _state = new();
    private readonly FakeSyncScopeFactory _scopes = new();
    private readonly FixedProbe _probe = new();
    private readonly FolderPair _pair = new(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
    private readonly InMemoryFileSystem _local;
    private readonly InMemoryRemoteStore _remote;

    /// <summary>
    /// Environment: one pair, both sides in-memory on the same virtual clock, server is "live".
    /// </summary>
    public BaselineEvidenceRunTests()
    {
        _local = new InMemoryFileSystem(_clock);
        _remote = new InMemoryRemoteStore(_clock);
        _scopes.Add(new SyncRunScope(_pair, _local, _remote, _probe));
    }

    [Fact]
    public async Task UploadRun_BaselineRowCarriesFreshRemoteEtag()
    {
        await SeedLocalAsync("a.txt", "A");

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await BaselineAsync(), static s => s.Path.ToString() == "a.txt");
        Assert.NotNull(row.ETag); // after the fix, After = local coordinate snapshot ⊕ fresh server ETag
        Assert.Equal(await RemoteEtagAsync("a.txt"), row.ETag);
    }

    [Fact]
    public async Task DownloadRun_BaselineRowCarriesEtagAndLocalEvidence()
    {
        await SeedRemoteAsync("b.txt", "B");

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await BaselineAsync(), static s => s.Path.ToString() == "b.txt");
        Assert.NotNull(row.ETag); // local write snapshot + ETag from the remote scan
        Assert.Equal(1, row.Size);
    }

    [Fact]
    public async Task UploadRun_SecondRunZeroOperations_NoEtagChurn()
    {
        await SeedLocalAsync("a.txt", "A");
        var run = Engine();

        Assert.True((await run.RunAsync(_pair.Id, CancellationToken.None)).IsSuccess);
        var second = await run.RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(0, second.Value.Planned); // strong evidence on both sides — the pair is quiet
    }

    [Fact]
    public async Task Resume_AppliedUpload_SeedsBaselineWithEtag()
    {
        await SeedLocalAsync("a.txt", "A");
        var op = new SyncOperation(
            OperationKind.UploadToRemote, RelativePath.Parse("a.txt"), null,
            new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 1));
        await _state.AppendJournalAsync(
            _pair.Id, new JournalEntry(1, op, JournalState.Planned, _clock.UtcNow), CancellationToken.None);

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None); // resume branch

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await BaselineAsync(), static s => s.Path.ToString() == "a.txt");
        Assert.NotNull(row.ETag); // journal → executor → baseline: evidence is not lost on resume
    }

    [Fact]
    public async Task Resume_BackfillsEtagsFromFreshRemoteScan()
    {
        // legacy row without an ETag + broken journal: resume also seeds the baseline from a fresh remote scan.
        await _state.ReplaceBaselineAsync(_pair.Id,
            [new FileSnapshot(RelativePath.Parse("c.txt"), EntryKind.File, 2, _clock.UtcNow)],
            CancellationToken.None);
        await SeedRemoteAsync("c.txt", "CC");
        await SeedLocalAsync("a.txt", "A");
        var op = new SyncOperation(
            OperationKind.UploadToRemote, RelativePath.Parse("a.txt"), null,
            new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 1));
        await _state.AppendJournalAsync(
            _pair.Id, new JournalEntry(1, op, JournalState.Planned, _clock.UtcNow), CancellationToken.None);

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var legacy = Assert.Single(await BaselineAsync(), static s => s.Path.ToString() == "c.txt");
        Assert.NotNull(legacy.ETag); // self-healing of legacy baseline rows
    }

    [Fact]
    public async Task InterruptedRun_ResumeSeedsAllBaselineEtags()
    {
        await SeedLocalAsync("a.txt", "A");
        await SeedLocalAsync("b.txt", "BB");
        var hang = new HangingRemoteStore(_remote);
        _scopes.Add(new SyncRunScope(_pair, _local, hang, _probe));
        var run = Engine();

        using var cts = new CancellationTokenSource();
        var first = run.RunAsync(_pair.Id, cts.Token);
        await hang.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.Equal(SyncRunOutcome.Cancelled, (await first).Value.Outcome);

        var resumed = await run.RunAsync(_pair.Id, CancellationToken.None);

        Assert.Equal(SyncRunOutcome.Success, resumed.Value.Outcome);
        Assert.All(
            (await BaselineAsync()).Where(static s => !s.IsDirectory),
            static row => Assert.NotNull(row.ETag)); // every applied step carried its evidence through
    }

    [Fact]
    public async Task LegacyRowNoEtag_BothSidesEditedSameSize_ConflictKeepsBothVersions()
    {
        // Engine without a hasher: mtime signal on one side + ETag on the other → conflict; both contents survive.
        await _state.ReplaceBaselineAsync(_pair.Id,
            [new FileSnapshot(RelativePath.Parse("d.txt"), EntryKind.File, 3, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))],
            CancellationToken.None);
        _clock.Set(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        await SeedLocalAsync("d.txt", "LLL");
        _clock.Set(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero));
        await SeedRemoteAsync("d.txt", "RRR");

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Conflicts);
        Assert.Contains(await LocalNamesAsync(), static n => n.Contains("conflicted copy"));
        Assert.Contains(await RemoteNamesAsync(), static n => n.Contains("conflicted copy"));
    }

    [Fact]
    public async Task WithHasher_IdenticalAdd_SeedsRow_PairedRunDoesNotRehash()
    {
        // Silent add-add now seeds a row via RecordConvergence; the paired run does 0 operations
        // and zero hasher calls (the evidence is cached in the baseline).
        await SeedLocalAsync("s.txt", "SAME");
        await SeedRemoteAsync("s.txt", "SAME");
        var hasher = new CountingHashProvider(new Sha256HashProvider());
        var run = Engine(hasher: hasher);

        var first = await run.RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(1, first.Value.Planned); // exactly one convergence op — no content moved
        Assert.Equal(0, first.Value.Conflicts);
        var row = Assert.Single(await BaselineAsync(), static s => s.Path.ToString() == "s.txt");
        Assert.NotNull(row.ETag); // local coordinate snapshot ⊕ both sides' evidence
        Assert.NotNull(row.ContentHash);
        var hashesAfterFirst = hasher.Count;
        Assert.True(hashesAfterFirst > 0); // the hasher proved identity on the first run

        var second = await run.RunAsync(_pair.Id, CancellationToken.None);

        Assert.Equal(0, second.Value.Planned);
        Assert.Equal(hashesAfterFirst, hasher.Count); // the row carries evidence — no more hashing needed
    }

    [Fact]
    public async Task WithHasher_ConvergedFile_DeleteLocal_PropagatesInsteadOfResurrecting()
    {
        // Acceptance: after convergence, a one-sided deletion propagates (stash + DeleteRemote)
        // instead of "resurrecting" as a "new file on the server".
        await SeedLocalAsync("s.txt", "SAME");
        await SeedRemoteAsync("s.txt", "SAME");
        var run = Engine(hasher: new Sha256HashProvider());

        Assert.True((await run.RunAsync(_pair.Id, CancellationToken.None)).IsSuccess);
        Assert.True((await _local.DeleteAsync(RelativePath.Parse("s.txt"), CancellationToken.None)).IsSuccess);

        var second = await run.RunAsync(_pair.Id, CancellationToken.None);

        Assert.Equal(SyncRunOutcome.Success, second.Value.Outcome);
        Assert.Equal(2, second.Value.Planned); // copy to stash + DeleteRemote — not an upload back
        Assert.DoesNotContain("s.txt", await RemoteNamesAsync());
        var locals = (await _local.SnapshotAsync(CancellationToken.None)).Value;
        Assert.DoesNotContain(locals, static s => s.Path.ToString() == "s.txt"); // did not resurrect at the pair root
        Assert.Contains(locals, static s => s.Path.StartsWith(RelativePath.Parse("FileFerry Trash"))); // protected 
    }

    [Fact]
    public async Task WithHasher_DifferentContentSameSize_KeepsBoth()
    {
        await SeedLocalAsync("s.txt", "AAAA");
        await SeedRemoteAsync("s.txt", "BBBB");

        var result = await Engine(hasher: new Sha256HashProvider()).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Conflicts);
        Assert.Contains(await LocalNamesAsync(), static n => n.Contains("conflicted copy"));
        Assert.Contains(await RemoteNamesAsync(), static n => n.Contains("conflicted copy"));
    }

    [Fact]
    public async Task WithHasher_LocalRename_MovesWithoutTrashOrReupload()
    {
        // the device renamed the file: the baseline row carries the server's evidence, the device
        // arrives with a new name and nothing to prove identity. The run hashes the rename shape
        // and moves the file on the server — no buffer copy, no delete, no re-upload of the same bytes
        await SeedRemoteAsync("big.bin", "PAYLOAD-PAYLOAD");
        await _state.ReplaceBaselineAsync(
            _pair.Id,
            [new FileSnapshot(RelativePath.Parse("big.bin"), EntryKind.File, 15, ETag: await RemoteEtagAsync("big.bin"))],
            CancellationToken.None);
        // the server's moment and the device's moment never coincide in the field —
        // the moved row's evidence must not inherit one side's bare mtime and call the other "changed"
        _clock.Advance(TimeSpan.FromMinutes(3));
        await SeedLocalAsync("moved.bin", "PAYLOAD-PAYLOAD");

        var result = await Engine(hasher: new Sha256HashProvider()).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Planned); // one MOVE and nothing else
        Assert.Equal(0, result.Value.Conflicts);
        Assert.Contains("moved.bin", await RemoteNamesAsync());
        Assert.DoesNotContain("big.bin", await RemoteNamesAsync());
        var locals = (await _local.SnapshotAsync(CancellationToken.None)).Value;
        Assert.DoesNotContain(locals, static s => s.Path.StartsWith(RelativePath.Parse("FileFerry Trash")));
        Assert.Contains("moved.bin", await LocalNamesAsync());
        Assert.DoesNotContain("big.bin", await LocalNamesAsync());

        var second = await Engine(hasher: new Sha256HashProvider()).RunAsync(_pair.Id, CancellationToken.None);
        Assert.Equal(0, second.Value.Planned); // the healed row holds the evidence — no churn after
    }

    [Fact]
    public async Task WithHasher_LocalRenameWithEditedContent_IsNotAMove()
    {
        // the mirror of trust: the hashing pass did its job and said "different" — an edited
        // rename is not a move: the stale server row goes the honest deletion road (buffer first),
        // the edited content travels up
        await SeedRemoteAsync("doc.txt", "AAAA");
        await _state.ReplaceBaselineAsync(
            _pair.Id,
            [new FileSnapshot(RelativePath.Parse("doc.txt"), EntryKind.File, 4, ETag: await RemoteEtagAsync("doc.txt"))],
            CancellationToken.None);
        await SeedLocalAsync("doc-new.txt", "BBBB"); // same size, other bytes — exactly what the hash decides

        var result = await Engine(hasher: new Sha256HashProvider()).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Conflicts);
        Assert.DoesNotContain("doc.txt", await RemoteNamesAsync());
        Assert.Contains("doc-new.txt", await RemoteNamesAsync());
        var locals = (await _local.SnapshotAsync(CancellationToken.None)).Value;
        Assert.Contains(locals, static s => s.Path.StartsWith(RelativePath.Parse("FileFerry Trash"))); // the old version is kept
    }

    [Fact]
    public async Task WithHasher_NoRenameInSameSizeWindow_HashesNothing()
    {
        // the pass pays only for rename shapes: a vanished baseline row and a new device row of
        // the same size. Unrelated content asks the hasher for nothing
        await SeedRemoteAsync("big.bin", "XXXXXXXXXX");
        await _state.ReplaceBaselineAsync(
            _pair.Id,
            [new FileSnapshot(RelativePath.Parse("big.bin"), EntryKind.File, 10, ETag: await RemoteEtagAsync("big.bin"))],
            CancellationToken.None);
        await SeedLocalAsync("small.txt", "ab"); // different size — no window, no reads
        var hasher = new CountingHashProvider(new Sha256HashProvider());

        var result = await Engine(hasher: hasher).RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, hasher.Count);
    }

    // ── mechanics ────────────────────────────────────────────────────────────────

    private SyncRun Engine(IHashProvider? hasher = null) =>
        new(
            _scopes,
            new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash),
            _state,
            _stash,
            _clock,
            handlers: null,
            observer: null,
            hasher: hasher);

    private Task<IReadOnlyList<FileSnapshot>> BaselineAsync() =>
        _state.LoadBaselineAsync(_pair.Id, CancellationToken.None);

    private async Task SeedLocalAsync(string path, string content) =>
        await _local.WriteAsync(
            RelativePath.Parse(path), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);

    private async Task SeedRemoteAsync(string name, string content) =>
        await _remote.SeedAsync(
            RemotePath.Parse("sync").Combine(name), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);

    private async Task<string?> RemoteEtagAsync(string name) =>
        (await _remote.ListAsync(RemotePath.Parse("sync"), CancellationToken.None)).Value
            .FirstOrDefault(e => e.Path.Name == name)?.ETag;

    private async Task<IReadOnlyList<string>> LocalNamesAsync() =>
        (await _local.SnapshotAsync(CancellationToken.None)).Value
            .Where(static s => !s.IsDirectory)
            .Select(static s => s.Path.Name).ToArray();

    private async Task<IReadOnlyList<string>> RemoteNamesAsync() =>
        (await _remote.ListAsync(RemotePath.Parse("sync"), CancellationToken.None)).Value
            .Where(static e => e.Kind != EntryKind.Directory)
            .Select(static e => e.Path.Name).ToArray();

    /// <summary>
    /// Counting hasher: proves that runs stop hashing once a row seeded with evidence exists.
    /// </summary>
    private sealed class CountingHashProvider(IHashProvider inner) : IHashProvider
    {
        private int _count;

        /// <summary>
        /// Number of calls (both methods).
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        public string Hash(ReadOnlySpan<byte> data)
        {
            Interlocked.Increment(ref _count);
            return inner.Hash(data);
        }

        public async Task<string> HashAsync(Stream stream, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return await inner.HashAsync(stream, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Remote side: the first write hangs until cancellation — simulates an interrupted run.
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
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
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
}
