using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Data;

namespace FileFerry.ContractTests.Data;

/// <summary>
/// Contract tests for the stash-index producer <see cref="StashIndexingLocalFileSystem"/>:
/// an index entry appears only on a successful copy into the stash root and is removed only on a
/// real copy delete; all other operations are transparent to the inner FS.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StashIndexingLocalFileSystemTests
{
    private static readonly RelativePath StashRoot = RelativePath.Parse("FileFerry Trash");
    private readonly Guid _pairId = Guid.NewGuid();
    private readonly MutableClock _clock = new();
    private readonly InMemoryFileSystem _inner = new();
    private readonly RecordingStashRepository _index = new();
    private readonly StashIndexingLocalFileSystem _sut;

    public StashIndexingLocalFileSystemTests() =>
        _sut = new StashIndexingLocalFileSystem(_inner, _index, _pairId, StashRoot, _clock);

    [Fact]
    public async Task Copy_into_stash_writes_index_with_origin_and_clock_time()
    {
        await Seed("Проєкти/звіт.md");
        var destination = RelativePath.Parse("FileFerry Trash/2026-11-11T00-00-00Z/Проєкти/звіт.md");

        var result = await _sut.CopyAsync(RelativePath.Parse("Проєкти/звіт.md"), destination, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var entry = Assert.Single(_index.Added);
        Assert.Equal(_pairId, entry.PairId);
        Assert.Equal(destination, entry.StashPath);
        Assert.Equal(RelativePath.Parse("Проєкти/звіт.md"), entry.OriginPath);
        Assert.Equal(_clock.UtcNow, entry.CreatedAt);
    }

    [Fact]
    public async Task Copy_outside_stash_leaves_index_untouched()
    {
        await Seed("a.txt");

        await _sut.CopyAsync(RelativePath.Parse("a.txt"), RelativePath.Parse("dir/b.txt"), CancellationToken.None);

        Assert.Empty(_index.Added);
    }

    [Fact]
    public async Task Failed_copy_writes_no_index()
    {
        var missing = await _sut.CopyAsync(RelativePath.Parse("немає.txt"), RelativePath.Parse("FileFerry Trash/d/немає.txt"), CancellationToken.None);

        Assert.True(missing.IsFailure);
        Assert.Empty(_index.Added);
    }

    [Fact]
    public async Task Delete_of_stash_copy_removes_index_entry()
    {
        var copy = RelativePath.Parse("FileFerry Trash/d/a.txt");
        await Seed("a.txt");
        await _sut.CopyAsync(RelativePath.Parse("a.txt"), copy, CancellationToken.None);

        await _sut.DeleteAsync(copy, CancellationToken.None);

        Assert.Equal([copy], _index.Removed);
    }

    [Fact]
    public async Task Delete_of_regular_path_does_not_touch_index()
    {
        await Seed("a.txt");
        await _sut.DeleteAsync(RelativePath.Parse("a.txt"), CancellationToken.None);

        Assert.Empty(_index.Removed);
    }

    // Regression scenario: a "vanished locally" delete copies the remote
    // version into the stash via WriteAsync (DownloadToLocal → stash path), not CopyAsync —
    // without write indexing the buffer is invisible to the trash-buffer screen, purge and restore.

    [Fact]
    public async Task Write_into_stash_indexes_origin_from_layout()
    {
        var destination = RelativePath.Parse("FileFerry Trash/20261114T190859/Docs/Test.md");

        var written = await _sut.WriteAsync(destination, new MemoryStream("з сервера"u8.ToArray()), CancellationToken.None);

        Assert.True(written.IsSuccess);
        var entry = Assert.Single(_index.Added);
        Assert.Equal(destination, entry.StashPath);
        Assert.Equal(RelativePath.Parse("Docs/Test.md"), entry.OriginPath); // root + stamp directory stripped
        Assert.Equal(_clock.UtcNow, entry.CreatedAt);
    }

    [Fact]
    public async Task Write_outside_stash_leaves_index_untouched()
    {
        var written = await _sut.WriteAsync(RelativePath.Parse("Docs/plain.md"), new MemoryStream([1]), CancellationToken.None);

        Assert.True(written.IsSuccess);
        Assert.Empty(_index.Added);
    }

    [Fact]
    public async Task Write_at_stash_layout_too_shallow_is_not_indexed()
    {
        // without an origin segment (root only or stamp only) — nothing to originate, no index;
        // this also guards against accidental writes "into service folders" outside the scheme
        await _sut.WriteAsync(RelativePath.Parse("FileFerry Trash/20261114T190859"), new MemoryStream([1]), CancellationToken.None);
        await _sut.WriteAsync(RelativePath.Parse("FileFerry Trash/звідось.txt"), new MemoryStream([1]), CancellationToken.None);

        Assert.Empty(_index.Added);
    }

    [Fact]
    public async Task Failed_write_into_stash_writes_no_index()
    {
        // the directory itself as write target — validated Conflict from the inner FS; index stays clean
        var dir = RelativePath.Parse("FileFerry Trash/20261114T190859/Docs");
        await _sut.CreateFolderAsync(dir, CancellationToken.None);

        var failed = await _sut.WriteAsync(dir, new MemoryStream([2]), CancellationToken.None);

        Assert.True(failed.IsFailure);
        Assert.Empty(_index.Added);
    }

    [Fact]
    public async Task Failed_delete_does_not_remove_index()
    {
        var gone = await _sut.DeleteAsync(RelativePath.Parse("FileFerry Trash/d/немає.txt"), CancellationToken.None);

        Assert.True(gone.IsFailure);
        Assert.Empty(_index.Removed);
    }

    /// <summary>
    /// Seed a file into the inner FS through the port directly (the decorator doesn't replace it).
    /// </summary>
    private Task Seed(string path) =>
        _inner.WriteAsync(RelativePath.Parse(path), new MemoryStream("вміст"u8.ToArray()), CancellationToken.None);

    /// <summary>
    /// <see cref="IStashRepository"/> spy: call sequence only, no persistence.
    /// </summary>
    private sealed class RecordingStashRepository : IStashRepository
    {
        public List<StashEntry> Added { get; } = [];
        public List<RelativePath> Removed { get; } = [];

        public Task AddAsync(StashEntry entry, CancellationToken ct)
        {
            Added.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StashEntry>> ListAsync(Guid pairId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StashEntry>>(Added);

        public Task RemoveAsync(Guid pairId, RelativePath stashPath, CancellationToken ct)
        {
            Removed.Add(stashPath);
            return Task.CompletedTask;
        }
    }
}
