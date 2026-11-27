using FileFerry.Data.Sqlite;

namespace FileFerry.Core.Tests.Data;

/// <summary>
/// The initializer's contract: a migration pass that fails (a disk hiccup, a cancelled pass)
/// must not be remembered — every later call retries and can succeed. The one-shot semantics
/// the store itself provides (a gate, a success latch) stay; only failure caching is forbidden.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DatabaseInitializerRetryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ff-db-init-").FullName;

    [Fact]
    public async Task LazyTaskPoisonsAFailedPassForever()
    {
        // the very shape the initializer used: Lazy<Task> remembers the first task, faulted
        // included — once the pass fails, every later caller awaits the same dead task
        var broken = true;
        var calls = 0;
        var ready = new Lazy<Task>(() =>
        {
            calls++;
            return broken
                ? Task.FromException(new IOException("disk hiccup"))
                : Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() => ready.Value);
        broken = false; // the hiccup passes — the retry should now succeed
        await Assert.ThrowsAsync<IOException>(() => ready.Value);
        Assert.Equal(1, calls); // never retried: the fault is cached until the process ends
    }

    [Fact]
    public async Task CancelledFirstPass_SecondPassMigrates()
    {
        var path = Path.Combine(_dir, "retry.db");
        var store = new SqliteStore(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.MigrateAsync(new CancellationToken(canceled: true)));

        await store.MigrateAsync(CancellationToken.None); // the same store, no restart: works
        Assert.True(File.Exists(path));
        Assert.True(await AppliedVersionsAsync(store) > 0);
    }

    [Fact]
    public async Task SuccessIsLatched_RepeatedPassesStampOnce()
    {
        var store = new SqliteStore(Path.Combine(_dir, "once.db"));

        await store.MigrateAsync(CancellationToken.None);
        await store.MigrateAsync(CancellationToken.None);
        await store.MigrateAsync(CancellationToken.None);

        var versions = await AppliedVersionsAsync(store);
        Assert.True(versions > 0);
        // repeated successful passes must not stamp a second copy of any version
        await using (var connection = await store.OpenAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(DISTINCT version) = COUNT(*) FROM schema_version;";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a temp tree that outlives the test is the OS's to reclaim
        }
    }

    private static async Task<int> AppliedVersionsAsync(SqliteStore store)
    {
        await using var connection = await store.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
