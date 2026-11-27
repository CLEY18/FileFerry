using System.Globalization;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;
using Microsoft.Data.Sqlite;

namespace FileFerry.Data.Sqlite;

/// <summary>
/// SQLite adapter for <see cref="IStashRepository"/> (<c>stash_meta</c> table): the index
/// of a pair's stash-buffer copies. Writes go through the single
/// <see cref="SqliteStore.Writes"/> queue; the <c>(pair_id, stash_path)</c> PK gives an
/// idempotent upsert (replanning the same delete does not multiply rows).
/// </summary>
public sealed class SqliteStashRepository : IStashRepository
{
    private readonly SqliteStore _store;

    /// <summary>
    /// Create the repository over the store.
    /// </summary>
    public SqliteStashRepository(SqliteStore store) => _store = store;

    /// <inheritdoc />
    public Task AddAsync(StashEntry entry, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO stash_meta(pair_id, stash_path, origin, created_at, tree_key)
                VALUES ($pair, $stash, $origin, $created,
                        COALESCE((SELECT local_tree_key FROM folder_pairs WHERE id = $pair), ''))
                ON CONFLICT(pair_id, stash_path) DO UPDATE SET
                    origin = $origin, created_at = $created;
                """;
            command.Parameters.AddWithValue("$pair", entry.PairId.ToString("D"));
            command.Parameters.AddWithValue("$stash", entry.StashPath.ToString());
            command.Parameters.AddWithValue("$origin", entry.OriginPath.ToString());
            command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc />
    public async Task<IReadOnlyList<StashEntry>> ListAsync(Guid pairId, CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stash_path, origin, created_at FROM stash_meta
            WHERE pair_id = $pair ORDER BY created_at DESC, stash_path;
            """;
        command.Parameters.AddWithValue("$pair", pairId.ToString("D"));

        var entries = new List<StashEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new StashEntry(
                pairId,
                RelativePath.Parse(reader.GetString(0)),
                RelativePath.Parse(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return entries;
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid pairId, RelativePath stashPath, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM stash_meta WHERE pair_id = $pair AND stash_path = $stash;";
            command.Parameters.AddWithValue("$pair", pairId.ToString("D"));
            command.Parameters.AddWithValue("$stash", stashPath.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrphanedStashEntry>> ListOrphansAsync(CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.pair_id, s.tree_key, s.stash_path, s.created_at FROM stash_meta s
            LEFT JOIN folder_pairs p ON p.id = s.pair_id
            WHERE p.id IS NULL
            ORDER BY s.created_at DESC, s.stash_path;
            """;

        var entries = new List<OrphanedStashEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new OrphanedStashEntry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                RelativePath.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return entries;
    }
}
