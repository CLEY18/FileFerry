using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;
using FileFerry.Data.Sqlite;

namespace FileFerry.Core.Tests.Data;

/// <summary>
/// The stash index outlives its pair: deleting a pair must not erase the buffer copies' rows
/// (that cascade used to orphan the files on disk beyond any purger's reach). The row stays,
/// carrying the tree it lives in, and the orphan listing is what the startup sweep reads.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StashOrphanIndexTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ff-stash-orphan-{Guid.NewGuid():n}.db");
    private SqliteStore _store = null!;
    private SqliteStashRepository _stash = null!;
    private SqliteFolderPairRepository _pairs = null!;

    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_dbPath);
        await _store.MigrateAsync(CancellationToken.None);
        _stash = new SqliteStashRepository(_store);
        _pairs = new SqliteFolderPairRepository(_store);

        var accountId = Guid.NewGuid();
        await new SqliteAccountRepository(_store)
            .SaveAsync(new Account(accountId, "https://cloud.example", "tetyana", "token-ref"), CancellationToken.None);
        _account = accountId;
    }

    private Guid _account;

    [Fact]
    public async Task PairDeleted_RowSurvives_AndIsListedAsOrphanWithItsTree()
    {
        var pairId = Guid.NewGuid();
        await _pairs.SaveAsync(
            new FolderPair(pairId, _account, "/sync/ferry", "content://tree/ferry", "Ferry"), CancellationToken.None);
        await _stash.AddAsync(
            new StashEntry(pairId, RelativePath.Parse("FileFerry Trash/a.txt"), RelativePath.Parse("a.txt"),
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        await _pairs.DeleteAsync(pairId, CancellationToken.None);

        // the row outlives the pair — that is the whole point: it is the address of a file
        // somebody can still restore until the TTL ends, then the orphan sweep collects it
        var orphans = await _stash.ListOrphansAsync(CancellationToken.None);
        var orphan = Assert.Single(orphans);
        Assert.Equal(pairId, orphan.PairId);
        Assert.Equal("content://tree/ferry", orphan.TreeKey); // the copy's address: which tree to open
        Assert.Equal(RelativePath.Parse("FileFerry Trash/a.txt"), orphan.StashPath);
    }

    [Fact]
    public async Task LivePairs_NeverAppearAsOrphans()
    {
        var pairId = Guid.NewGuid();
        await _pairs.SaveAsync(
            new FolderPair(pairId, _account, "/sync/keep", "content://tree/keep", "Keep"), CancellationToken.None);
        await _stash.AddAsync(
            new StashEntry(pairId, RelativePath.Parse("FileFerry Trash/b.txt"), RelativePath.Parse("b.txt"),
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        Assert.Empty(await _stash.ListOrphansAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AccountDeleted_ItsPairsCopiesBecomeOrphansToo()
    {
        // the account cascade deletes the pairs — the copies' rows must survive that second
        // cascade as well, or the sign-out would orphan buffer files exactly like a pair delete
        var pairId = Guid.NewGuid();
        await _pairs.SaveAsync(
            new FolderPair(pairId, _account, "/sync/bye", "content://tree/bye", "Bye"), CancellationToken.None);
        await _stash.AddAsync(
            new StashEntry(pairId, RelativePath.Parse("FileFerry Trash/c.txt"), RelativePath.Parse("c.txt"),
                DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        await new SqliteAccountRepository(_store).DeleteAsync(_account, CancellationToken.None);

        var orphan = Assert.Single(await _stash.ListOrphansAsync(CancellationToken.None));
        Assert.Equal("content://tree/bye", orphan.TreeKey);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }
}
