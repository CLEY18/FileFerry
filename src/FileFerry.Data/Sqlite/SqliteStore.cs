using Microsoft.Data.Sqlite;

namespace FileFerry.Data.Sqlite;

/// <summary>
/// SQLite connection factory + idempotent migration applier. Configures the
/// file once: WAL (parallel readers over one writer) and foreign keys.
/// <see cref="Writes"/> is the single writer queue for the whole database: every
/// repository publishes changes here, so two write streams (e.g. run state plus a pair
/// pause from the UI) never meet on SQLITE_BUSY.
/// </summary>
public sealed class SqliteStore : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _migrateGate = new(1, 1);
    private volatile bool _migrated;

    /// <summary>
    /// The database's single writer (single-writer); lives for the whole
    /// lifetime of the store.
    /// </summary>
    public WriteQueue Writes { get; } = new();

    /// <summary>
    /// Create the store for a.db file; ":memory:" works for tests, but with the shared
    /// cache it then behaves as one pooled file shared by all connections.
    /// </summary>
    public SqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Cache = SqliteCacheMode.Shared };
        _connectionString = builder.ToString();
    }

    /// <summary>
    /// Open a fresh connection with the mandatory pragmas (applied before any query).
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>
    /// Apply all migrations not yet applied; repeat calls are a no-op (latched flag plus
    /// SQL IF NOT EXISTS).
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await _migrateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = Schema.VersionTableSql;
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var applied = await GetAppliedVersionsAsync(connection, ct).ConfigureAwait(false);
            for (var i = 0; i < Schema.Migrations.Count; i++)
            {
                var version = i + 1;
                if (applied.Contains(version))
                {
                    continue; // already applied — a repeat run does nothing
                }

                await using var tx = connection.BeginTransaction();
                foreach (var statement in SplitStatements(Schema.Migrations[i]))
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = tx;
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var stamp = connection.CreateCommand())
                {
                    stamp.Transaction = tx;
                    stamp.CommandText = "INSERT INTO schema_version(version) VALUES ($v);";
                    stamp.Parameters.AddWithValue("$v", version);
                    await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            _migrated = true;
        }
        finally
        {
            _migrateGate.Release();
        }
    }

    /// <summary>
    /// Guarantees a one-time migration on the first connection.
    /// </summary>
    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        if (!_migrated)
        {
            await MigrateAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Numbers of the already-applied versions.
    /// </summary>
    private static async Task<HashSet<int>> GetAppliedVersionsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var versions = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    /// <summary>
    /// Split a migration into single statements (SQLite does not reliably execute several
    /// statements in one CommandText).
    /// </summary>
    private static IEnumerable<string> SplitStatements(string migration) =>
        migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Finish the writer queue (drains the tail); connection pools are the owner's side.
    /// </summary>
    public async ValueTask DisposeAsync() => await Writes.DisposeAsync().ConfigureAwait(false);
}
