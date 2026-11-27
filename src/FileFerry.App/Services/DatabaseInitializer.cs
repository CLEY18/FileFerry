using FileFerry.Data.Sqlite;

namespace FileFerry.App.Services;

/// <summary>
/// Database preparation before use (migrations): a failed pass must stay retryable, so every
/// call goes to <see cref="SqliteStore.MigrateAsync"/> — the store holds the one-shot (a gate
/// plus a success latch, a no-op once the schema is current). A self-scheduled <c>Lazy&lt;Task&gt;</c>
/// is forbidden here on purpose: it caches its first task faulted or not, so one transient
/// failure would be replayed to every later caller for the life of the process.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly SqliteStore _store;

    /// <summary>
    /// Creates the initializer over the single <see cref="SqliteStore"/>.
    /// </summary>
    public DatabaseInitializer(SqliteStore store) => _store = store;

    /// <summary>
    /// Schema readiness: a cheap no-op once migrated; after a failed pass — a real retry.
    /// </summary>
    public Task EnsureReadyAsync() => _store.MigrateAsync(CancellationToken.None);
}
