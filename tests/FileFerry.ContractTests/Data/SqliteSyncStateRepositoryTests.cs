using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Data.Sqlite;
using Microsoft.Data.Sqlite;

namespace FileFerry.ContractTests.Data;

/// <summary>
/// Unit tests for the SQLite state: repeated migration = no-op, WAL, round-trips
/// baseline/journal/run-log, single-writer serialization.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqliteSyncStateRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fileferry-test-{Guid.NewGuid():n}.db");
    private SqliteStore _store = null!;
    private SqliteSyncStateRepository _repo = null!;

    /// <summary>
    /// Open the store and the repository over a temporary database file.
    /// </summary>
    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_dbPath);
        await _store.MigrateAsync(CancellationToken.None);
        _repo = new SqliteSyncStateRepository(_store);
    }

    /// <summary>
    /// Close the repository and delete the database file.
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
    /// No-op implementation (cleanup happens in <see cref="DisposeAsync"/>).
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Creates the account and pair parent rows — the FKs require them before child records.
    /// </summary>
    private async Task SeedPairAsync(Guid pairId)
    {
        await using var connection = await _store.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO accounts (id, base_url, user_name, token_ref) VALUES ('a1', 'https://nc.example', 'user', 'ref');
            INSERT OR IGNORE INTO folder_pairs (id, account_id, remote_root, local_tree_key, display_name, schedule, conflict)
                VALUES ($pair, 'a1', '/sync', 'tree', 'Sync', 0, 0);
            """;
        command.Parameters.AddWithValue("$pair", pairId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Migrate_Twice_IsNoOp()
    {
        // "second run = no-op": a repeated MigrateAsync adds no schema_version rows (count stays stable).
        await using var probe = await _store.OpenAsync(CancellationToken.None);
        await using var probeCmd = probe.CreateCommand();
        probeCmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var before = (long)probeCmd.ExecuteScalar()!;

        await _store.MigrateAsync(CancellationToken.None); // second time — no exception and no changes

        await using var connection = await _store.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_version;";
        Assert.Equal(before, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task Database_RunsInWalMode()
    {
        await using var connection = await _store.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task Baseline_RoundTrips_AndReplaces()
    {
        var pair = Guid.NewGuid();
        await SeedPairAsync(pair);
        var first = new FileSnapshot(RelativePath.Parse("a/b.txt"), EntryKind.File, 42,
            new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero), "\"etag1\"", "deadbeef");

        await _repo.ReplaceBaselineAsync(pair, [first], CancellationToken.None);
        var loaded = await _repo.LoadBaselineAsync(pair, CancellationToken.None);
        Assert.Equal(first, Assert.Single(loaded));

        await _repo.ReplaceBaselineAsync(pair, [], CancellationToken.None);
        Assert.Empty(await _repo.LoadBaselineAsync(pair, CancellationToken.None));
    }

    [Fact]
    public async Task Journal_PendingLifecycle()
    {
        var pair = Guid.NewGuid();
        await SeedPairAsync(pair);
        var operation = new SyncOperation(OperationKind.UploadToRemote, RelativePath.Parse("x.txt"));

        await _repo.AppendJournalAsync(pair, new JournalEntry(1, operation, JournalState.Planned, DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal([JournalState.Planned], (await _repo.LoadJournalAsync(pair, CancellationToken.None)).Select(static e => e.State));

        // upsert by (pair, seq): the same Seq updates the state — resume skips Applied
        await _repo.AppendJournalAsync(pair, new JournalEntry(1, operation, JournalState.Applied, DateTimeOffset.UtcNow), CancellationToken.None);
        var after = await _repo.LoadJournalAsync(pair, CancellationToken.None);
        Assert.Equal([JournalState.Applied], after.Select(static e => e.State));
        Assert.DoesNotContain(after, static e => e.State != JournalState.Applied);

        await _repo.AppendJournalAsync(pair, new JournalEntry(2, operation, JournalState.Planned, DateTimeOffset.UtcNow), CancellationToken.None);
        await _repo.ClearJournalAsync(pair, CancellationToken.None);
        Assert.Empty(await _repo.LoadJournalAsync(pair, CancellationToken.None));
    }

    [Fact]
    public async Task Journal_AfterEvidence_RoundTripsForResumeSeeding()
    {
        // Resume seeds the baseline from the After evidence of applied steps — it must survive serialization.
        var pair = Guid.NewGuid();
        await SeedPairAsync(pair);
        var modified = new DateTimeOffset(2026, 11, 14, 18, 30, 0, TimeSpan.Zero);
        var operation = new SyncOperation(
            OperationKind.UploadToRemote, RelativePath.Parse("x.txt"), null,
            new FileSnapshot(RelativePath.Parse("x.txt"), EntryKind.File, 31, modified, ETag: "\"E9\"", ContentHash: "abc123"));

        await _repo.AppendJournalAsync(pair, new JournalEntry(1, operation, JournalState.Planned, DateTimeOffset.UtcNow), CancellationToken.None);
        await _repo.AppendJournalAsync(pair, new JournalEntry(1, operation, JournalState.Applied, DateTimeOffset.UtcNow), CancellationToken.None);

        var entry = Assert.Single(await _repo.LoadJournalAsync(pair, CancellationToken.None));
        var after = Assert.IsType<FileSnapshot>(entry.Operation.After);
        Assert.Equal(31, after.Size);
        Assert.Equal("\"E9\"", after.ETag);
        Assert.Equal("abc123", after.ContentHash);
        Assert.Equal(modified, after.ModifiedAt);

        var empty = new SyncOperation(OperationKind.DeleteLocal, RelativePath.Parse("gone.txt"));
        await _repo.AppendJournalAsync(pair, new JournalEntry(2, empty, JournalState.Applied, DateTimeOffset.UtcNow), CancellationToken.None);
        var gone = (await _repo.LoadJournalAsync(pair, CancellationToken.None)).Single(static e => e.Operation.Kind == OperationKind.DeleteLocal);
        Assert.Null(gone.Operation.After); // no evidence — the round-trip is correct too
    }

    [Fact]
    public async Task RunLog_RoundTrips_WithEvents_AndUpserts()
    {
        var pair = Guid.NewGuid();
        await SeedPairAsync(pair);
        var run = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var log = new SyncRunLog(run, pair, started, null, SyncRunOutcome.Running,
            [new SyncEvent(started, EventKind.RunStarted), new SyncEvent(started.AddSeconds(1), EventKind.ConflictDetected, SyncEventOutcome.Warning, RelativePath.Parse("a.txt"))]);

        await _repo.SaveRunLogAsync(log, CancellationToken.None);
        var recent = await _repo.RecentRunsAsync(pair, 10, CancellationToken.None);
        var stored = Assert.Single(recent);
        Assert.Equal(SyncRunOutcome.Running, stored.Outcome);
        Assert.Equal(2, stored.Events.Count);
        Assert.Equal("a.txt", stored.Events[1].Path!.ToString());

        await _repo.SaveRunLogAsync(log with { FinishedAt = started.AddMinutes(1), Outcome = SyncRunOutcome.Success, Events = [new SyncEvent(started, EventKind.AppliedBaseline, SyncEventOutcome.Success)] }, CancellationToken.None);
        var updated = Assert.Single(await _repo.RecentRunsAsync(pair, 10, CancellationToken.None));
        Assert.Equal(SyncRunOutcome.Success, updated.Outcome);
        Assert.Single(updated.Events);
    }

    [Fact]
    public async Task RunLog_Events_RoundTripStructuredKindAndArgs()
    {
        // Instead of free text — kind + args; the App renders the text, the DB stores intent
        var pair = Guid.NewGuid();
        await SeedPairAsync(pair);
        var started = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var log = new SyncRunLog(Guid.NewGuid(), pair, started, started.AddSeconds(5), SyncRunOutcome.Success,
            [
                new SyncEvent(started, EventKind.RunStarted),
                new SyncEvent(started, EventKind.PlanSummary,
                    Args: new Dictionary<string, string> { ["count"] = "4" }),
                new SyncEvent(started, EventKind.ConflictDetected, SyncEventOutcome.Warning,
                    RelativePath.Parse("a.txt"), new Dictionary<string, string> { ["conflict"] = "AddAdd" }),
            ]);

        await _repo.SaveRunLogAsync(log, CancellationToken.None);

        var stored = Assert.Single(await _repo.RecentRunsAsync(pair, 10, CancellationToken.None));
        Assert.Equal(3, stored.Events.Count);
        Assert.Equal(EventKind.RunStarted, stored.Events[0].Kind);
        Assert.Equal(EventKind.PlanSummary, stored.Events[1].Kind);
        Assert.Equal("4", stored.Events[1].Args!["count"]);
        Assert.Equal(EventKind.ConflictDetected, stored.Events[2].Kind);
        Assert.Equal("AddAdd", stored.Events[2].Args!["conflict"]);
        Assert.Equal("a.txt", stored.Events[2].Path!.ToString());
    }

    [Fact]
    public async Task RunLog_OfADeletedPair_IsRejected_TheDatabaseStaysWritable()
    {
        // The run-log hangs off the pair as its parent: a write about a run of a deleted pair has
        // no parent and SQLite rejects it — the rejection stays inside that one write.
        var gone = Guid.NewGuid();
        var alive = Guid.NewGuid();
        await SeedPairAsync(gone);
        await SeedPairAsync(alive);
        var started = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

        await _repo.SaveRunLogAsync(
            new SyncRunLog(Guid.NewGuid(), gone, started, started.AddSeconds(1), SyncRunOutcome.Success,
                [new SyncEvent(started, EventKind.RunStarted)]), CancellationToken.None);
        await using (var connection = await _store.OpenAsync(CancellationToken.None))
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM folder_pairs WHERE id = $pair;";
            delete.Parameters.AddWithValue("$pair", gone.ToString());
            await delete.ExecuteNonQueryAsync();
        }

        Assert.Empty(await _repo.RecentRunsAsync(gone, 10, CancellationToken.None)); // deleting a pair cascades its history

        await Assert.ThrowsAsync<SqliteException>(() => _repo.SaveRunLogAsync(
            new SyncRunLog(Guid.NewGuid(), gone, started, started.AddSeconds(1), SyncRunOutcome.Failed,
                [new SyncEvent(started, EventKind.RunFailed, SyncEventOutcome.Error)]), CancellationToken.None));

        await _repo.SaveRunLogAsync(
            new SyncRunLog(Guid.NewGuid(), alive, started, started.AddSeconds(1), SyncRunOutcome.Success,
                [new SyncEvent(started, EventKind.RunStarted)]), CancellationToken.None);
        Assert.Single(await _repo.RecentRunsAsync(alive, 10, CancellationToken.None)); // the orphan row's rejection is not a broken database
    }

    [Fact]
    public async Task Migration_V4_WipesPreB15RunHistory_AndDropsMessageColumn()
    {
        // Pre-release clarification: a v2-state database (textual run-log, no kind) after
        // migration must have neither old events nor the text column at all — "as if it never existed".
        // Repeated migration — no-op. The run-log is illustrative: baseline/operation journal stay untouched.
        var path = Path.Combine(Path.GetTempPath(), $"fileferry-v4-{Guid.NewGuid():n}.db");
        try
        {
            await using (var raw = new SqliteConnection($"Data Source={path}"))
            {
                await raw.OpenAsync(CancellationToken.None);
                await using var seed = raw.CreateCommand();
                seed.CommandText = """
                    CREATE TABLE schema_version (version INTEGER NOT NULL PRIMARY KEY);
                    INSERT INTO schema_version VALUES (1), (2);
                    CREATE TABLE sync_log (
                        run_id TEXT PRIMARY KEY, pair_id TEXT NOT NULL, started_at TEXT NOT NULL,
                        finished_at TEXT NULL, outcome INTEGER NOT NULL, seq INTEGER NOT NULL);
                    CREATE TABLE sync_events (
                        run_id TEXT NOT NULL, seq INTEGER NOT NULL, at TEXT NOT NULL, path TEXT NULL,
                        outcome INTEGER NOT NULL, message TEXT NOT NULL, PRIMARY KEY (run_id, seq));
                    INSERT INTO sync_log VALUES ('r1', 'p1', '2026-10-01T09:00:00+00:00', NULL, 0, 1);
                    INSERT INTO sync_events VALUES ('r1', 0, '2026-10-01T09:00:00+00:00', NULL, 1, 'Run finished.');
                    """;
                await seed.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.MigrateAsync(CancellationToken.None);
                await store.MigrateAsync(CancellationToken.None); // idempotence — like every migration

                await using var check = await store.OpenAsync(CancellationToken.None);
                await using (var rows = check.CreateCommand())
                {
                    rows.CommandText = "SELECT (SELECT COUNT(*) FROM sync_log) + (SELECT COUNT(*) FROM sync_events);";
                    Assert.Equal(0L, Convert.ToInt64(await rows.ExecuteScalarAsync(CancellationToken.None)));
                }

                await using (var cols = check.CreateCommand())
                {
                    // message dropped; kind+params (v3) remain
                    cols.CommandText =
                        "SELECT COUNT(*) FROM pragma_table_info('sync_events') WHERE name IN ('message', 'kind', 'params');";
                    Assert.Equal(2L, Convert.ToInt64(await cols.ExecuteScalarAsync(CancellationToken.None)));
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
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
    }

    [Fact]
    public async Task WriteQueue_Serializes_AndPreservesOrder()
    {
        await using var queue = new WriteQueue();
        var log = new List<int>();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => queue.RunAsync(() =>
            {
                lock (log)
                {
                    log.Add(i);
                }

                return Task.CompletedTask;
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 50), log); // one executor — queue order preserved
    }

    [Fact]
    public async Task WriteQueue_FailureIsIsolatedPerItem()
    {
        await using var queue = new WriteQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(() => throw new InvalidOperationException("write failure")));

        var reached = false;
        await queue.RunAsync(() =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        Assert.True(reached); // the queue stays alive after someone else's failure
    }
}
