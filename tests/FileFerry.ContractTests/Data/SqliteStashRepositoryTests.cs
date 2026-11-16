using System.Globalization;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;
using FileFerry.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace FileFerry.ContractTests.Data;

/// <summary>
/// Contract tests for <see cref="IStashRepository"/>: field round-trip, "newest first" order,
/// per-pair isolation, removal of only the given path, and idempotent upsert on PK
/// <c>(pair_id, stash_path)</c> (a repeated delete plan doesn't spawn extra indexes).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteStashRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fileferry-stash-{Guid.NewGuid():n}.db");
    private SqliteStore _store = null!;
    private SqliteStashRepository _stash = null!;
    private Guid _pairA;
    private Guid _pairB;

    /// <summary>
    /// Open the store and create the two container pairs (FK stash_meta → folder_pairs).
    /// </summary>
    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_dbPath);
        await _store.MigrateAsync(CancellationToken.None);

        var accountId = Guid.NewGuid();
        await new SqliteAccountRepository(_store)
            .SaveAsync(new Account(accountId, "https://cloud.example", "nadia", $"token-ref-{accountId:N}"), CancellationToken.None);
        var pairs = new SqliteFolderPairRepository(_store);
        _pairA = Guid.NewGuid();
        _pairB = Guid.NewGuid();
        await pairs.SaveAsync(new FolderPair(_pairA, accountId, "/sync/a", "tree-a", "A"), CancellationToken.None);
        await pairs.SaveAsync(new FolderPair(_pairB, accountId, "/sync/b", "tree-b", "B"), CancellationToken.None);

        _stash = new SqliteStashRepository(_store);
    }

    /// <summary>
    /// Finish the writer, clear connection pools, and remove the database files.
    /// </summary>
    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_dbPath) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // the OS removes temp files itself
            }
        }
    }

    /// <summary>
    /// Build a copy-index entry with an explicit time for deterministic order/round-trip.
    /// </summary>
    private StashEntry Entry(Guid pairId, string stash, string origin, string createdAt) =>
        new(pairId, RelativePath.Parse(stash), RelativePath.Parse(origin),
            DateTimeOffset.Parse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    [Fact]
    public async Task Add_then_list_round_trips_all_fields()
    {
        var entry = Entry(_pairA, "FileFerry Trash/2026-11-11T10-00-00Z/Проєкти/звіт.md", "Проєкти/звіт.md", "2026-11-11T10:00:00.0000000+02:00");
        await _stash.AddAsync(entry, CancellationToken.None);

        var loaded = Assert.Single(await _stash.ListAsync(_pairA, CancellationToken.None));

        Assert.Equal(entry with { PairId = _pairA }, loaded);
    }

    [Fact]
    public async Task List_returns_newest_first()
    {
        await _stash.AddAsync(Entry(_pairA, "FileFerry Trash/old/f.txt", "old/f.txt", "2026-11-01T00:00:00.0000000+00:00"), CancellationToken.None);
        await _stash.AddAsync(Entry(_pairA, "FileFerry Trash/new/g.txt", "new/g.txt", "2026-11-10T00:00:00.0000000+00:00"), CancellationToken.None);

        var loaded = await _stash.ListAsync(_pairA, CancellationToken.None);

        Assert.Equal(["FileFerry Trash/new/g.txt", "FileFerry Trash/old/f.txt"], loaded.Select(e => e.StashPath.ToString()));
    }

    [Fact]
    public async Task List_is_scoped_per_pair()
    {
        await _stash.AddAsync(Entry(_pairA, "FileFerry Trash/x/a.txt", "x/a.txt", "2026-11-10T00:00:00.0000000+00:00"), CancellationToken.None);
        await _stash.AddAsync(Entry(_pairB, "FileFerry Trash/x/b.txt", "x/b.txt", "2026-11-10T00:00:00.0000000+00:00"), CancellationToken.None);

        var loadedA = await _stash.ListAsync(_pairA, CancellationToken.None);

        Assert.Equal(["FileFerry Trash/x/a.txt"], loadedA.Select(e => e.StashPath.ToString()));
    }

    [Fact]
    public async Task Remove_drops_only_the_given_path()
    {
        var first = Entry(_pairA, "FileFerry Trash/d1/a.txt", "d1/a.txt", "2026-11-09T00:00:00.0000000+00:00");
        var second = Entry(_pairA, "FileFerry Trash/d2/a.txt", "d2/a.txt", "2026-11-10T00:00:00.0000000+00:00");
        await _stash.AddAsync(first, CancellationToken.None);
        await _stash.AddAsync(second, CancellationToken.None);

        await _stash.RemoveAsync(_pairA, first.StashPath, CancellationToken.None);

        var loaded = await _stash.ListAsync(_pairA, CancellationToken.None);
        Assert.Equal(["FileFerry Trash/d2/a.txt"], loaded.Select(e => e.StashPath.ToString()));
    }

    [Fact]
    public async Task Add_same_stash_path_twice_upserts_single_row()
    {
        await _stash.AddAsync(Entry(_pairA, "FileFerry Trash/d/a.txt", "d/a.txt", "2026-11-09T00:00:00.0000000+00:00"), CancellationToken.None);
        await _stash.AddAsync(Entry(_pairA, "FileFerry Trash/d/a.txt", "d/перейменований.txt", "2026-11-10T00:00:00.0000000+00:00"), CancellationToken.None);

        var loaded = Assert.Single(await _stash.ListAsync(_pairA, CancellationToken.None));

        Assert.Equal(RelativePath.Parse("d/перейменований.txt"), loaded.OriginPath);
    }
}
