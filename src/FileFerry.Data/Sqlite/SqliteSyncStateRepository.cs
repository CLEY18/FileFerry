using System.Globalization;
using System.Text.Json;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;
using Microsoft.Data.Sqlite;

namespace FileFerry.Data.Sqlite;

/// <summary>
/// SQLite adapter for <see cref="ISyncStateRepository"/>: baseline,
/// write-ahead journal and run log. Reads use their own connection (WAL allows parallel
/// ones); every write goes through the <see cref="WriteQueue"/>.
/// Baseline replacement happens in one transaction — the UoW ground for invariant.
/// </summary>
public sealed class SqliteSyncStateRepository : ISyncStateRepository
{
    private readonly SqliteStore _store;

    /// <summary>
    /// Create the repository over the store (migrations apply on first access).
    /// </summary>
    public SqliteSyncStateRepository(SqliteStore store) => _store = store;

    /// <inheritdoc cref="ISyncStateRepository.LoadBaselineAsync"/>
    public async Task<IReadOnlyList<FileSnapshot>> LoadBaselineAsync(Guid pairId, CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, kind, size, modified_at, etag, hash FROM sync_baseline WHERE pair_id = $pair ORDER BY path;";
        command.Parameters.AddWithValue("$pair", pairId.ToString());

        var result = new List<FileSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new FileSnapshot(
                RelativePath.Parse(reader.GetString(0)),
                (EntryKind)reader.GetInt32(1),
                reader.GetInt64(2),
                ReadTime(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    /// <inheritdoc cref="ISyncStateRepository.ReplaceBaselineAsync"/>
    public Task ReplaceBaselineAsync(Guid pairId, IReadOnlyList<FileSnapshot> baseline, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM sync_baseline WHERE pair_id = $pair;";
                clear.Parameters.AddWithValue("$pair", pairId.ToString());
                await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var snapshot in baseline)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO sync_baseline(pair_id, path, kind, size, modified_at, etag, hash) VALUES ($pair, $path, $kind, $size, $mod, $etag, $hash);";
                insert.Parameters.AddWithValue("$pair", pairId.ToString());
                insert.Parameters.AddWithValue("$path", snapshot.Path.ToString());
                insert.Parameters.AddWithValue("$kind", (int)snapshot.Kind);
                insert.Parameters.AddWithValue("$size", snapshot.Size);
                insert.Parameters.AddWithValue("$mod", WriteTime(snapshot.ModifiedAt));
                insert.Parameters.AddWithValue("$etag", (object?)snapshot.ETag ?? DBNull.Value);
                insert.Parameters.AddWithValue("$hash", (object?)snapshot.ContentHash ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc cref="ISyncStateRepository.AppendJournalAsync"/>
    public Task AppendJournalAsync(Guid pairId, JournalEntry entry, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO sync_journal(pair_id, seq, kind, path, source_path, state, at,
                                                    after_kind, after_size, after_mtime, after_etag, after_hash)
                VALUES ($pair, $seq, $kind, $path, $src, $state, $at,
                        $akind, $asize, $amod, $aetag, $ahash);
                """;
            command.Parameters.AddWithValue("$pair", pairId.ToString());
            command.Parameters.AddWithValue("$seq", entry.Seq);
            command.Parameters.AddWithValue("$kind", (int)entry.Operation.Kind);
            command.Parameters.AddWithValue("$path", entry.Operation.Path.ToString());
            command.Parameters.AddWithValue("$src", (object?)entry.Operation.SourcePath?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", (int)entry.State);
            command.Parameters.AddWithValue("$at", WriteTime(entry.At));
            var after = entry.Operation.After; // evidence of the applied step survives a restart
            command.Parameters.AddWithValue("$akind", after is null ? DBNull.Value : (int)after.Kind);
            command.Parameters.AddWithValue("$asize", (object?)after?.Size ?? DBNull.Value);
            command.Parameters.AddWithValue("$amod", WriteTime(after?.ModifiedAt));
            command.Parameters.AddWithValue("$aetag", (object?)after?.ETag ?? DBNull.Value);
            command.Parameters.AddWithValue("$ahash", (object?)after?.ContentHash ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc cref="ISyncStateRepository.LoadJournalAsync"/>
    public async Task<IReadOnlyList<JournalEntry>> LoadJournalAsync(Guid pairId, CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT seq, kind, path, source_path, state, at, after_kind, after_size, after_mtime, after_etag, after_hash
            FROM sync_journal WHERE pair_id = $pair ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$pair", pairId.ToString());

        var entries = new List<JournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var kind = (OperationKind)reader.GetInt32(1);
            var path = RelativePath.Parse(reader.GetString(2));
            var source = reader.IsDBNull(3) ? null : RelativePath.Parse(reader.GetString(3));
            var after = reader.IsDBNull(6)
                ? null
                : new FileSnapshot(
                    path,
                    (EntryKind)reader.GetInt32(6),
                    reader.GetInt64(7),
                    ReadTime(reader, 8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10));
            entries.Add(new JournalEntry(
                reader.GetInt64(0),
                new SyncOperation(kind, path, source, after),
                (JournalState)reader.GetInt32(4),
                ReadTime(reader, 5)!.Value));
        }

        return entries;
    }

    /// <inheritdoc cref="ISyncStateRepository.ClearJournalAsync"/>
    public Task ClearJournalAsync(Guid pairId, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sync_journal WHERE pair_id = $pair;";
            command.Parameters.AddWithValue("$pair", pairId.ToString());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc cref="ISyncStateRepository.SaveRunLogAsync"/>
    public Task SaveRunLogAsync(SyncRunLog runLog, CancellationToken ct) =>
        _store.Writes.RunAsync(async () =>
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO sync_log(run_id, pair_id, started_at, finished_at, outcome, seq)
                    VALUES ($run, $pair, $start, $finish, $outcome, $seq)
                    ON CONFLICT(run_id) DO UPDATE SET finished_at = $finish, outcome = $outcome, seq = $seq;
                    """;
                upsert.Parameters.AddWithValue("$run", runLog.RunId.ToString());
                upsert.Parameters.AddWithValue("$pair", runLog.PairId.ToString());
                upsert.Parameters.AddWithValue("$start", WriteTime(runLog.StartedAt));
                upsert.Parameters.AddWithValue("$finish", WriteTime(runLog.FinishedAt));
                upsert.Parameters.AddWithValue("$outcome", (int)runLog.Outcome);
                upsert.Parameters.AddWithValue("$seq", runLog.Events.Count);
                await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var clearEvents = connection.CreateCommand())
            {
                clearEvents.Transaction = tx;
                clearEvents.CommandText = "DELETE FROM sync_events WHERE run_id = $run;";
                clearEvents.Parameters.AddWithValue("$run", runLog.RunId.ToString());
                await clearEvents.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            for (var i = 0; i < runLog.Events.Count; i++)
            {
                var e = runLog.Events[i];
                await using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO sync_events(run_id, seq, at, path, outcome, kind, params) VALUES ($run, $seq, $at, $path, $outcome, $kind, $params);";
                insert.Parameters.AddWithValue("$run", runLog.RunId.ToString());
                insert.Parameters.AddWithValue("$seq", i);
                insert.Parameters.AddWithValue("$at", WriteTime(e.At));
                insert.Parameters.AddWithValue("$path", (object?)e.Path?.ToString() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$outcome", (int)e.Outcome);
                insert.Parameters.AddWithValue("$kind", (int)e.Kind); // only the intent — the App renders the text
                insert.Parameters.AddWithValue(
                    "$params", e.Args is { Count: > 0 } args ? JsonSerializer.Serialize(args) : (object)DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        });

    /// <inheritdoc cref="ISyncStateRepository.RecentRunsAsync"/>
    public async Task<IReadOnlyList<SyncRunLog>> RecentRunsAsync(Guid pairId, int limit, CancellationToken ct)
    {
        await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT run_id, pair_id, started_at, finished_at, outcome FROM sync_log WHERE pair_id = $pair ORDER BY started_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$pair", pairId.ToString());
        command.Parameters.AddWithValue("$limit", limit);

        var runs = new List<SyncRunLog>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                runs.Add(new SyncRunLog(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    ReadTime(reader, 2)!.Value,
                    ReadTime(reader, 3),
                    (SyncRunOutcome)reader.GetInt32(4),
                    []));
            }
        }

        for (var i = 0; i < runs.Count; i++)
        {
            runs[i] = runs[i] with { Events = await LoadEventsAsync(connection, runs[i].RunId, ct).ConfigureAwait(false) };
        }

        return runs;
    }

    /// <summary>
    /// Event phrase arguments from JSON; missing or corrupt ones yield
    /// <see langword="null"/> (the screen must not break).
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadEventArgs(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Events of one run in chronological order.
    /// </summary>
    private static async Task<IReadOnlyList<SyncEvent>> LoadEventsAsync(SqliteConnection connection, Guid runId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT at, path, outcome, kind, params FROM sync_events WHERE run_id = $run ORDER BY seq;";
        command.Parameters.AddWithValue("$run", runId.ToString());

        var events = new List<SyncEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // kind is mandatory per the write contract (since v4); an empty one is a corrupt
            // row: the journal screen must never crash, so such a row is simply skipped
            if (reader.IsDBNull(3))
            {
                continue;
            }

            events.Add(new SyncEvent(
                ReadTime(reader, 0)!.Value,
                (EventKind)reader.GetInt32(3),
                (SyncEventOutcome)reader.GetInt32(2),
                reader.IsDBNull(1) ? null : RelativePath.Parse(reader.GetString(1)),
                ReadEventArgs(reader, 4)));
        }

        return events;
    }

    /// <summary>
    /// Time → durable text format (NULL when absent).
    /// </summary>
    private static object WriteTime(DateTimeOffset? value) =>
        value?.ToString("o", CultureInfo.InvariantCulture) ?? (object)DBNull.Value;

    /// <summary>
    /// Time from the text format; <see langword="null"/> for NULL.
    /// </summary>
    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
