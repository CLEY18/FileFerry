namespace FileFerry.Data.Sqlite;

/// <summary>
/// Versioned idempotent schema migrations. Each script is part of one
/// transaction; the <c>schema_version</c> table makes a repeated run a no-op.
/// </summary>
internal static class Schema
{
    /// <summary>
    /// Numbered migrations; new ones may only be appended (number = index + 1).
    /// </summary>
    internal static readonly IReadOnlyList<string> Migrations =
    [
        // v1 — the five base tables + the operation journal
        """
        CREATE TABLE IF NOT EXISTS accounts (
            id         TEXT PRIMARY KEY,
            base_url   TEXT NOT NULL,
            user_name  TEXT NOT NULL,
            token_ref  TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS folder_pairs (
            id              TEXT PRIMARY KEY,
            account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            remote_root     TEXT NOT NULL,
            local_tree_key  TEXT NOT NULL,
            display_name    TEXT NOT NULL,
            schedule        INTEGER NOT NULL,
            conflict        INTEGER NOT NULL,
            paused          INTEGER NOT NULL DEFAULT 0,
            wifi_only       INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS sync_baseline (
            pair_id     TEXT NOT NULL REFERENCES folder_pairs(id) ON DELETE CASCADE,
            path        TEXT NOT NULL,
            kind        INTEGER NOT NULL,          -- 0 File, 1 Directory
            size        INTEGER NOT NULL,
            modified_at TEXT NULL,                 -- ISO-8601 round-trip
            etag        TEXT NULL,
            hash        TEXT NULL,
            PRIMARY KEY (pair_id, path)
        );

        CREATE TABLE IF NOT EXISTS sync_log (
            run_id      TEXT PRIMARY KEY,
            pair_id     TEXT NOT NULL REFERENCES folder_pairs(id) ON DELETE CASCADE,
            started_at  TEXT NOT NULL,
            finished_at TEXT NULL,
            outcome     INTEGER NOT NULL,
            seq         INTEGER NOT NULL           -- event order within the run
        );

        CREATE TABLE IF NOT EXISTS sync_events (
            run_id  TEXT NOT NULL REFERENCES sync_log(run_id) ON DELETE CASCADE,
            seq     INTEGER NOT NULL,
            at      TEXT NOT NULL,
            path    TEXT NULL,
            outcome INTEGER NOT NULL,
            message TEXT NOT NULL,
            PRIMARY KEY (run_id, seq)
        );

        CREATE TABLE IF NOT EXISTS sync_journal (
            pair_id     TEXT NOT NULL REFERENCES folder_pairs(id) ON DELETE CASCADE,
            seq         INTEGER NOT NULL,
            kind        INTEGER NOT NULL,          -- OperationKind
            path        TEXT NOT NULL,
            source_path TEXT NULL,
            state       INTEGER NOT NULL,          -- JournalState
            at          TEXT NOT NULL,
            PRIMARY KEY (pair_id, seq)
        );

        CREATE TABLE IF NOT EXISTS stash_meta (
            pair_id    TEXT NOT NULL REFERENCES folder_pairs(id) ON DELETE CASCADE,
            stash_path TEXT NOT NULL,              -- copy path relative to the local root
            origin     TEXT NOT NULL,              -- the removed record
            created_at TEXT NOT NULL,              -- basis for the TTL purge
            PRIMARY KEY (pair_id, stash_path)
        );
        """,

        // v2 — evidence of the applied step (After) is persisted in the journal;
        // otherwise resume would not seed the baseline and the ETag would be lost forever.
        """
        ALTER TABLE sync_journal ADD COLUMN after_kind INTEGER NULL;
        ALTER TABLE sync_journal ADD COLUMN after_size INTEGER NULL;
        ALTER TABLE sync_journal ADD COLUMN after_mtime TEXT NULL;
        ALTER TABLE sync_journal ADD COLUMN after_etag TEXT NULL;
        ALTER TABLE sync_journal ADD COLUMN after_hash TEXT NULL;
        """,

        // v3 — a journal event stores the intent (kind + params-JSON), not rendered text.
        """
        ALTER TABLE sync_events ADD COLUMN kind INTEGER NULL;
        ALTER TABLE sync_events ADD COLUMN params TEXT NULL;
        """,

        // v4 — pre-release clarification: the run-log is presentation data only, unrelated
        // to any stop-rule, so pre-clarification texts are safe to lose. Wipe the run
        // history and drop the text column "as if it never existed": kind is now mandatory
        // per the write contract. On fresh DBs the DELETEs are no-ops; DROP COLUMN works
        // because Microsoft.Data.Sqlite ships a recent SQLite.
        """
        DELETE FROM sync_events;
        DELETE FROM sync_log;
        ALTER TABLE sync_events DROP COLUMN message;
        """,

        // v5 — the stash index outlives its pair. The cascade on stash_meta erased a deleted
        // pair's copy rows silently, orphaning the buffer files beyond every purger while the
        // delete dialog promised thirty retrievable days. Rebuild the table without the
        // foreign key and with a tree_key: the address of the tree each copy physically
        // sits in, joined from the live pair at insert time (and backfilled here while the
        // pairs still exist). A row whose pair is gone is what the orphan sweep reads.
        """
        CREATE TABLE stash_meta_v5 (
            pair_id    TEXT NOT NULL,
            stash_path TEXT NOT NULL,
            origin     TEXT NOT NULL,
            created_at TEXT NOT NULL,
            tree_key   TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (pair_id, stash_path)
        );

        INSERT INTO stash_meta_v5 (pair_id, stash_path, origin, created_at, tree_key)
        SELECT s.pair_id, s.stash_path, s.origin, s.created_at,
               COALESCE((SELECT f.local_tree_key FROM folder_pairs f WHERE f.id = s.pair_id), '')
        FROM stash_meta s;

        DROP TABLE stash_meta;

        ALTER TABLE stash_meta_v5 RENAME TO stash_meta;
        """,
    ];

    /// <summary>
    /// Scaffolding: the single counter table of applied versions.
    /// </summary>
    internal const string VersionTableSql =
        "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY);";
}
