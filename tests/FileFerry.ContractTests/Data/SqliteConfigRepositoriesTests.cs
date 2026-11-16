using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace FileFerry.ContractTests.Data;

/// <summary>
/// Contract tests for the configuration repositories: CRUD round-trip, upsert without losing
/// child data (REPLACE hazard), creation order, FK cascade on account delete, and rejection
/// of pairs with an unknown account.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteConfigRepositoriesTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fileferry-cfg-{Guid.NewGuid():n}.db");
    private SqliteStore _store = null!;
    private SqliteAccountRepository _accounts = null!;
    private SqliteFolderPairRepository _pairs = null!;

    /// <summary>
    /// Open the store and both repositories over a temporary database file.
    /// </summary>
    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_dbPath);
        await _store.MigrateAsync(CancellationToken.None);
        _accounts = new SqliteAccountRepository(_store);
        _pairs = new SqliteFolderPairRepository(_store);
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
    /// Build an account with deterministic fields for round-trip assertions.
    /// </summary>
    private static Account NewAccount(Guid id) =>
        new(id, "https://cloud.example:8443", "petro", $"token-ref-{id:N}");

    /// <summary>
    /// Build a pair with all-non-default values (enums and bools are round-trip checked).
    /// </summary>
    private static FolderPair NewPair(Guid id, Guid accountId, string name = "Документи") =>
        new(id, accountId, "/sync/ docs", "tree-key", name, SyncSchedule.Hourly, ConflictStrategy.KeepBoth, Paused: true, WifiOnly: false);

    /// <summary>
    /// Helper: save the account through the single write queue.
    /// </summary>
    private Task SaveAsync(Account account) => _accounts.SaveAsync(account, CancellationToken.None);

    /// <summary>
    /// Helper: save the pair through the single write queue.
    /// </summary>
    private Task SaveAsync(FolderPair pair) => _pairs.SaveAsync(pair, CancellationToken.None);

    [Fact]
    public async Task Save_then_find_account_round_trips_all_fields()
    {
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);

        var loaded = await _accounts.FindAsync(account.Id, CancellationToken.None);

        Assert.Equal(account, loaded);
    }

    [Fact]
    public async Task Find_missing_account_returns_null()
    {
        Assert.Null(await _accounts.FindAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Save_account_twice_updates_in_place()
    {
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);
        await SaveAsync(account with { BaseUrl = "http://203.0.113.10:8080", TokenRef = "ref-rotated" });

        var loaded = await _accounts.FindAsync(account.Id, CancellationToken.None);
        Assert.Equal("http://203.0.113.10:8080", loaded!.BaseUrl);
        Assert.Equal("ref-rotated", loaded.TokenRef);
        Assert.Single(await _accounts.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Save_pair_round_trips_enums_and_bools()
    {
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);
        var pair = NewPair(Guid.NewGuid(), account.Id);
        await SaveAsync(pair);

        var loaded = await _pairs.FindAsync(pair.Id, CancellationToken.None);

        Assert.Equal(pair, loaded);
    }

    [Fact]
    public async Task Delete_missing_rows_are_noop()
    {
        await _accounts.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
        await _pairs.DeleteAsync(Guid.NewGuid(), CancellationToken.None); // no exception
    }

    [Fact]
    public async Task List_async_keeps_creation_order_across_re_saves()
    {
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);
        var first = NewPair(Guid.NewGuid(), account.Id, "A");
        var second = NewPair(Guid.NewGuid(), account.Id, "B");
        var third = NewPair(Guid.NewGuid(), account.Id, "C");
        await SaveAsync(first);
        await SaveAsync(second);
        await SaveAsync(third);

        await SaveAsync(second with { Paused = true }); // upsert must not reorder

        var listing = await _pairs.ListAsync(CancellationToken.None);
        Assert.Equal([first.Id, second.Id, third.Id], listing.Select(p => p.Id));
    }

    [Fact]
    public async Task Saving_existing_pair_keeps_baseline_rows()
    {
        // Regression for the REPLACE hazard: INSERT OR REPLACE would delete the pair row,
        // and the sync_baseline FK cascade would take the baseline down with it.
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);
        var pair = NewPair(Guid.NewGuid(), account.Id);
        await SaveAsync(pair);
        var state = new SqliteSyncStateRepository(_store);
        var baseline = new[]
        {
            new FileSnapshot(RelativePath.Parse("a.txt"), EntryKind.File, 3, null, "etag-a", null),
        };
        await state.ReplaceBaselineAsync(pair.Id, baseline, CancellationToken.None);

        await SaveAsync(pair with { DisplayName = "Документи 2" });

        var kept = await state.LoadBaselineAsync(pair.Id, CancellationToken.None);
        Assert.Single(kept);
        Assert.Equal("a.txt", kept[0].Path.ToString());
    }

    [Fact]
    public async Task Saving_pair_with_unknown_account_is_rejected_by_fk()
    {
        var orphan = NewPair(Guid.NewGuid(), Guid.NewGuid());

        var error = await Assert.ThrowsAsync<SqliteException>(() => SaveAsync(orphan));

        Assert.Equal(19, error.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task Deleting_account_cascades_to_pairs()
    {
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);
        var pair = NewPair(Guid.NewGuid(), account.Id);
        await SaveAsync(pair);

        await _accounts.DeleteAsync(account.Id, CancellationToken.None);

        Assert.Null(await _pairs.FindAsync(pair.Id, CancellationToken.None));
        Assert.Empty(await _pairs.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Account_row_never_stores_token_material()
    {
        // the DB stores only TokenRef; check the raw table row.
        var account = NewAccount(Guid.NewGuid());
        await SaveAsync(account);

        await using var connection = await _store.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_ref FROM accounts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", account.Id.ToString("D"));
        var stored = (string?) await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(account.TokenRef, stored);
    }
}
