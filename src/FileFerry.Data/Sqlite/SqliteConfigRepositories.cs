using FileFerry.Core.Domain;
using FileFerry.Core.Ports;
using Microsoft.Data.Sqlite;

namespace FileFerry.Data.Sqlite;

/// <summary>
/// SQLite adapter for <see cref="IAccountRepository"/>: connection metadata (accounts table).
/// Reads use their own connection (WAL); writes go only through <see cref="SqliteStore.Writes"/>.
/// Save is an <c>ON CONFLICT DO UPDATE</c> upsert, not <c>INSERT OR REPLACE</c>:
/// REPLACE would delete the row and the FK cascade would destroy child pairs and baselines
/// on a plain account update.
/// </summary>
public sealed class SqliteAccountRepository : IAccountRepository
{
    private const string SelectColumns = "SELECT id, base_url, user_name, token_ref FROM accounts";

    private readonly SqliteStore _store;

    /// <summary>
    /// Create the repository over the store (migrations run on first connection).
    /// </summary>
    public SqliteAccountRepository(SqliteStore store) => _store = store;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY rowid;"; // "creation order": upsert preserves rowid

        var accounts = new List<Account>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            accounts.Add(Map(reader));
        }

        return accounts;
    }

    /// <inheritdoc />
    public async Task<Account?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public Task SaveAsync(Account entity, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts(id, base_url, user_name, token_ref) VALUES ($id, $url, $user, $ref)
                ON CONFLICT(id) DO UPDATE SET base_url = $url, user_name = $user, token_ref = $ref;
                """;
            command.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
            command.Parameters.AddWithValue("$url", entity.BaseUrl);
            command.Parameters.AddWithValue("$user", entity.UserName);
            command.Parameters.AddWithValue("$ref", entity.TokenRef);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc />
    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM accounts WHERE id = $id;"; // missing row is a no-op
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <summary>
    /// SELECT row → account aggregate.
    /// </summary>
    private static Account Map(SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3));
}

/// <summary>
/// SQLite adapter for <see cref="IFolderPairRepository"/>: sync folder pairs
/// (folder_pairs table). Same REPLACE-free upsert; the <c>account_id</c> FK is enforced
/// per connection (pragma foreign_keys), and deleting an account cascades to its pairs.
/// </summary>
public sealed class SqliteFolderPairRepository : IFolderPairRepository
{
    private const string SelectColumns =
        "SELECT id, account_id, remote_root, local_tree_key, display_name, schedule, conflict, paused, wifi_only FROM folder_pairs";

    private readonly SqliteStore _store;

    /// <summary>
    /// Create the repository over the store.
    /// </summary>
    public SqliteFolderPairRepository(SqliteStore store) => _store = store;

    /// <inheritdoc />
    public async Task<IReadOnlyList<FolderPair>> ListAsync(CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY rowid;";

        var pairs = new List<FolderPair>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            pairs.Add(Map(reader));
        }

        return pairs;
    }

    /// <inheritdoc />
    public async Task<FolderPair?> FindAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public Task SaveAsync(FolderPair entity, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO folder_pairs(id, account_id, remote_root, local_tree_key, display_name, schedule, conflict, paused, wifi_only)
                VALUES ($id, $account, $remote, $tree, $name, $schedule, $conflict, $paused, $wifi)
                ON CONFLICT(id) DO UPDATE SET
                    account_id = $account, remote_root = $remote, local_tree_key = $tree,
                    display_name = $name, schedule = $schedule, conflict = $conflict,
                    paused = $paused, wifi_only = $wifi;
                """;
            command.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
            command.Parameters.AddWithValue("$account", entity.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$remote", entity.RemoteRootPath);
            command.Parameters.AddWithValue("$tree", entity.LocalTreeKey);
            command.Parameters.AddWithValue("$name", entity.DisplayName);
            command.Parameters.AddWithValue("$schedule", (int) entity.Schedule);
            command.Parameters.AddWithValue("$conflict", (int) entity.Conflict);
            command.Parameters.AddWithValue("$paused", entity.Paused ? 1 : 0);
            command.Parameters.AddWithValue("$wifi", entity.WifiOnly ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc />
    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM folder_pairs WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <summary>
    /// SELECT row → pair aggregate (bools stored as 0/1, enums as int).
    /// </summary>
    private static FolderPair Map(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            (SyncSchedule) reader.GetInt32(5),
            (ConflictStrategy) reader.GetInt32(6),
            reader.GetInt32(7) != 0,
            reader.GetInt32(8) != 0);
}
