using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// A listing can legitimately carry the same name in two Unicode normalisation forms - a server
/// on a byte-preserving filesystem keeps them as two files, while the path model folds both to one
/// canonical name. The index behind the pipeline must never fall over on that duplicate key: it
/// folds to a deterministic winner and notes the name, so the run plans or notes, never dies.
/// </summary>
[Trait("Category", "SyncScenario")]
public sealed class NameFormCollisionRunTests
{
    // "resume.txt" with accented e's: the composed (NFC) and the decomposed (NFD) spelling.
    private const string CanonicalName = "re\u00E9sum\u00E9.txt";
    private const string DecomposedName = "ree\u0301sume\u0301.txt";

    private readonly MutableClock _clock = new();
    private readonly StashPolicy _stash = new(new StashPolicyOptions());
    private readonly InMemorySyncStateRepository _state = new();
    private readonly FakeSyncScopeFactory _scopes = new();
    private readonly FixedProbe _probe = new();
    private readonly FolderPair _pair = new(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
    private readonly InMemoryFileSystem _local;
    private readonly InMemoryRemoteStore _remote;

    public NameFormCollisionRunTests()
    {
        _local = new InMemoryFileSystem(_clock);
        _remote = new InMemoryRemoteStore(_clock);
    }

    [Fact]
    public async Task BothSpellingsOnServer_RunSucceeds_NotesTheName_AndLandsTheFile()
    {
        await SeedRemoteAsync(CanonicalName, "ORIGINAL");
        AttachRemoteTwin(CanonicalName, DecomposedName, twinSize: 4242,
            twinModifiedAt: _clock.UtcNow.AddMinutes(10), twinETag: "etag-nfd");

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        Assert.Equal(1, result.Value.Planned); // the two spellings are one path for the model: one download
        Assert.Contains(CanonicalName, await LocalNamesAsync());
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        var note = Assert.Single(log.Events, static e => e.Kind == EventKind.NameFormCollision);
        Assert.Equal(SyncEventOutcome.Warning, note.Outcome);
        Assert.Equal(RelativePath.Parse(DecomposedName), note.Path); // the note names the folded path
    }

    [Fact]
    public async Task SpellingsStillThere_EveryNextRunSucceeds_AndKeepsNoting()
    {
        // the death loop this row is about: before the fold existed, each run died the same way
        await SeedRemoteAsync(CanonicalName, "ORIGINAL");
        AttachRemoteTwin(CanonicalName, DecomposedName, twinSize: 4242,
            twinModifiedAt: _clock.UtcNow.AddMinutes(10), twinETag: "etag-nfd");

        for (var run = 1; run <= 3; run++)
        {
            var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);
            Assert.True(result.IsSuccess, $"run {run}: " + (result.IsFailure ? result.Error.Message : string.Empty));
        }

        var logs = await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None);
        Assert.Equal(3, logs.Count);
        Assert.All(logs, static log =>
            Assert.Contains(log.Events, static e => e.Kind == EventKind.NameFormCollision));
    }

    [Fact]
    public void WinnerIsTheNewerSpelling_WhicheverOrderTheScanReturns()
    {
        // determinism: the winner must follow metadata, not listing order - a provider free to
        // answer in any order must never make the same tree plan two different plans
        var canonical = RelativePath.Parse(CanonicalName);
        var nfc = new FileSnapshot(canonical, EntryKind.File, 8, _clock.UtcNow, "etag-nfc");
        var nfd = new FileSnapshot(RelativePath.Parse(DecomposedName), EntryKind.File, 4242,
            _clock.UtcNow.AddMinutes(10), "etag-nfd");
        var planner = new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash);

        var forward = Plan(planner, [nfc, nfd]);
        var flipped = Plan(planner, [nfd, nfc]);

        Assert.True(forward.IsSuccess);
        Assert.True(flipped.IsSuccess);
        var op = Assert.Single(forward.Value.Operations);
        Assert.Equal(OperationKind.DownloadToLocal, op.Kind);
        Assert.Equal(4242, op.After!.Size); // the newer spelling wins in both orders
        Assert.Equal(forward.Value.Operations.Count, flipped.Value.Operations.Count);
        Assert.Equal(op.After!.Size, flipped.Value.Operations[0].After!.Size);
        Assert.Equal([canonical], forward.Value.NameCollisions); // and the name is noted
        Assert.Equal(forward.Value.NameCollisions, flipped.Value.NameCollisions);
    }

    [Fact]
    public async Task LocalSideCarriesBothSpellings_RunSucceeds_AndNotes()
    {
        await SeedLocalAsync(CanonicalName, "LOCAL-CONTENT");
        _scopes.Add(new SyncRunScope(
            _pair, new SpellingTwinLocal(_local, CanonicalName, DecomposedName, twinSize: 4242),
            _remote, _probe));

        var result = await Engine().RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        Assert.Equal(SyncRunOutcome.Success, result.Value.Outcome);
        var log = (await _state.RecentRunsAsync(_pair.Id, 5, CancellationToken.None)).Single();
        Assert.Contains(log.Events, static e => e.Kind == EventKind.NameFormCollision);
    }

    [Fact]
    public async Task ConflictResolutionWithBothSpellingsInScan_Succeeds_KeepsServerContent()
    {
        // the resolution pipeline indexes the same scans - the colliding name must not kill it either
        await SeedLocalAsync("doc.txt", "LOCAL-V");
        await SeedRemoteAsync("doc.txt", "SERVER-V");
        await SeedRemoteAsync(CanonicalName, "ACCENTED");
        AttachRemoteTwin(CanonicalName, DecomposedName, twinSize: 4242,
            twinModifiedAt: _clock.UtcNow.AddMinutes(10), twinETag: "etag-nfd");

        var conflictRun = await Engine().RunAsync(_pair.Id, CancellationToken.None);
        Assert.True(conflictRun.IsSuccess, conflictRun.IsFailure ? conflictRun.Error.Message : string.Empty);
        Assert.Equal(1, conflictRun.Value.Conflicts);

        var resolved = await Engine().ResolveConflictAsync(
            _pair.Id, RelativePath.Parse("doc.txt"), KeepSide.Server, CancellationToken.None);
        Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Message : string.Empty);
        Assert.Equal("SERVER-V", await ReadLocalAsync("doc.txt"));
    }

    [Fact]
    public async Task HasherOnCollisionListing_RunSucceeds()
    {
        // the evidence hatch indexes the scans too - it must fold the same way, not throw
        await SeedRemoteAsync(CanonicalName, "ORIGINAL");
        AttachRemoteTwin(CanonicalName, DecomposedName, twinSize: 4242,
            twinModifiedAt: _clock.UtcNow.AddMinutes(10), twinETag: "etag-nfd");

        var result = await Engine(hasher: new Sha256HashProvider())
            .RunAsync(_pair.Id, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
    }

    // ---------------------------------------------------------------- environment

    private void AttachRemoteTwin(
        string sibling, string twinName, long twinSize, DateTimeOffset twinModifiedAt, string twinETag) =>
        _scopes.Add(new SyncRunScope(
            _pair, _local,
            new SpellingTwinRemote(_remote, sibling, twinName, twinSize, twinModifiedAt, twinETag),
            _probe));

    private Result<PlanningResult, SyncError> Plan(
        SyncPlanner planner, List<FileSnapshot> remote) =>
        planner.Plan(
            _pair,
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(Array.Empty<FileSnapshot>()),
            Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(remote),
            Array.Empty<FileSnapshot>(),
            _clock.UtcNow);

    private SyncRun Engine(IHashProvider? hasher = null) =>
        new(
            _scopes,
            new SyncPlanner(new MergePlanner(new KeepBothStrategy(), _stash), _stash),
            _state,
            _stash,
            _clock,
            hasher: hasher);

    private async Task SeedLocalAsync(string path, string content) =>
        await _local.WriteAsync(
            RelativePath.Parse(path), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);

    private async Task SeedRemoteAsync(string name, string content) =>
        await _remote.SeedAsync(
            RemotePath.Parse("sync").Combine(name), new MemoryStream(Encoding.UTF8.GetBytes(content)), CancellationToken.None);

    private async Task<IReadOnlyList<string>> LocalNamesAsync() =>
        (await _local.SnapshotAsync(CancellationToken.None)).Value
            .Select(static s => s.Path.ToString()).ToArray();

    private async Task<string> ReadLocalAsync(string path)
    {
        var read = await _local.ReadAsync(RelativePath.Parse(path), CancellationToken.None);
        Assert.True(read.IsSuccess);
        using var reader = new StreamReader(read.Value, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// A remote listing that answers every folder with the sibling file plus its second-spelling
    /// twin - what a byte-preserving server really returns for a name two clients normalised apart.
    /// </summary>
    private sealed class SpellingTwinRemote : IWritableRemoteStore
    {
        private readonly IWritableRemoteStore _inner;
        private readonly string _sibling;
        private readonly string _twinName;
        private readonly long _twinSize;
        private readonly DateTimeOffset _twinModifiedAt;
        private readonly string _twinETag;

        internal SpellingTwinRemote(
            IWritableRemoteStore inner, string sibling, string twinName,
            long twinSize, DateTimeOffset twinModifiedAt, string twinETag)
        {
            _inner = inner;
            _sibling = sibling;
            _twinName = twinName;
            _twinSize = twinSize;
            _twinModifiedAt = twinModifiedAt;
            _twinETag = twinETag;
        }

        public async Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct)
        {
            var listing = await _inner.ListAsync(folder, ct).ConfigureAwait(false);
            if (listing.IsFailure)
            {
                return listing;
            }

            var children = listing.Value.ToList();
            var siblingEntry = children.Find(e => e.Kind == EntryKind.File && e.Path.Name == _sibling);
            if (siblingEntry is not null)
            {
                children.Add(siblingEntry with
                {
                    Path = folder.Combine(_twinName),
                    Size = _twinSize,
                    ModifiedAt = _twinModifiedAt,
                    ETag = _twinETag,
                });
            }

            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Success(children);
        }

        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) =>
            _inner.ReadAsync(file, ct);

        public Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct) =>
            _inner.WriteAsync(file, content, ct);

        public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct) =>
            _inner.CreateFolderAsync(folder, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) =>
            _inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) =>
            _inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// A local scan listing one file under both its spellings (an ext4-backed tree can hold both).
    /// </summary>
    private sealed class SpellingTwinLocal : ILocalFileSystem
    {
        private readonly ILocalFileSystem _inner;
        private readonly RelativePath _sibling;
        private readonly string _twinName;
        private readonly long _twinSize;

        internal SpellingTwinLocal(ILocalFileSystem inner, string sibling, string twinName, long twinSize)
        {
            _inner = inner;
            _sibling = RelativePath.Parse(sibling);
            _twinName = twinName;
            _twinSize = twinSize;
        }

        public async Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> SnapshotAsync(CancellationToken ct)
        {
            var scan = await _inner.SnapshotAsync(ct).ConfigureAwait(false);
            if (scan.IsFailure)
            {
                return scan;
            }

            var files = scan.Value.ToList();
            var sibling = files.Find(s => !s.IsDirectory && s.Path == _sibling);
            if (sibling is not null)
            {
                files.Add(sibling with { Path = RelativePath.Parse(_twinName), Size = _twinSize });
            }

            return Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(files);
        }

        public Task<Result<Stream, SyncError>> ReadAsync(RelativePath path, CancellationToken ct) =>
            _inner.ReadAsync(path, ct);

        public Task<Result<bool, SyncError>> ExistsAsync(RelativePath path, CancellationToken ct) =>
            _inner.ExistsAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> WriteAsync(RelativePath path, Stream content, CancellationToken ct) =>
            _inner.WriteAsync(path, content, ct);

        public Task<Result<Ok, SyncError>> CreateFolderAsync(RelativePath path, CancellationToken ct) =>
            _inner.CreateFolderAsync(path, ct);

        public Task<Result<FileSnapshot, SyncError>> CopyAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            _inner.CopyAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RelativePath from, RelativePath to, CancellationToken ct) =>
            _inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RelativePath path, CancellationToken ct) =>
            _inner.DeleteAsync(path, ct);
    }
}
