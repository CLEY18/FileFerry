using FileFerry.Data.Sqlite;

namespace FileFerry.App.Services;

/// <summary>
/// Idempotent database preparation at startup (migrations): exactly one <c>MigrateAsync</c> pass
/// per session, repeated calls await the same <see cref="Task"/>.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly Lazy<Task> _ready;

    /// <summary>
    /// Creates the initializer over the single <see cref="SqliteStore"/>.
    /// </summary>
    public DatabaseInitializer(SqliteStore store) =>
        _ready = new Lazy<Task>(
            () => store.MigrateAsync(CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Schema readiness; the first call performs the migrations, the rest just await it.
    /// </summary>
    public Task EnsureReadyAsync() => _ready.Value;
}
