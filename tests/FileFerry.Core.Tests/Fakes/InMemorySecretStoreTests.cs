using FileFerry.Core.Fakes;

namespace FileFerry.Core.Tests.Fakes;

/// <summary>
/// Semantics of the in-memory fake secret store (<c>ISecretStore</c> contract, invariant):
/// overwrite, removal, missing key → null; keys are case-sensitive.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InMemorySecretStoreTests
{
    [Fact]
    public async Task Unknown_key_reads_null()
    {
        var store = new InMemorySecretStore();

        Assert.Null(await store.GetAsync("token/1", CancellationToken.None));
    }

    [Fact]
    public async Task Set_then_get_returns_value_and_overwrite_replaces()
    {
        var store = new InMemorySecretStore();

        await store.SetAsync("token/1", "abc", CancellationToken.None);
        Assert.Equal("abc", await store.GetAsync("token/1", CancellationToken.None));

        await store.SetAsync("token/1", "xyz", CancellationToken.None);
        Assert.Equal("xyz", await store.GetAsync("token/1", CancellationToken.None));
        Assert.Single(store.Keys);
    }

    [Fact]
    public async Task Remove_drops_value_and_missing_key_is_noop()
    {
        var store = new InMemorySecretStore();
        await store.SetAsync("token/1", "abc", CancellationToken.None);

        await store.RemoveAsync("token/1", CancellationToken.None);
        await store.RemoveAsync("token/1", CancellationToken.None); // no exception

        Assert.Null(await store.GetAsync("token/1", CancellationToken.None));
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task Keys_are_case_sensitive()
    {
        var store = new InMemorySecretStore();

        await store.SetAsync("Token/1", "upper", CancellationToken.None);

        Assert.Null(await store.GetAsync("token/1", CancellationToken.None));
        Assert.Equal("upper", await store.GetAsync("Token/1", CancellationToken.None));
    }

    [Fact]
    public async Task Blank_keys_are_rejected()
    {
        var store = new InMemorySecretStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetAsync(" ", CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(string.Empty, "v", CancellationToken.None));
    }
}
