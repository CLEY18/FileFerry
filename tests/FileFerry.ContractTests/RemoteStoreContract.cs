using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.ContractTests;

/// <summary>
/// Contract suite for <see cref="IWritableRemoteStore"/> implementations: fake-WebDAV,
/// the real WebDAV adapter, and the in-memory "server" must behave identically.
/// Test bodies live in <see cref="RemoteStoreContractSuite"/> — integration classes with
/// <see cref="IntegrationFactAttribute"/> reuse the same semantics without inheritance.
/// </summary>
[Trait("Category", "Contract")]
public abstract class RemoteStoreContract
{
    /// <summary>
    /// A clean "server" for each test.
    /// </summary>
    protected abstract IWritableRemoteStore Create();

    /// <summary>
    /// Write → read round-trip; the entry returns size and a fresh ETag.
    /// </summary>
    [Fact]
    public Task Write_ThenRead_RoundTrips() => RemoteStoreContractSuite.WriteThenReadRoundTrips(Create);

    /// <summary>
    /// A second write of the same file changes the ETag (engine diff and ETag-pruning rely on it).
    /// </summary>
    [Fact]
    public Task Write_Again_ChangesEtag() => RemoteStoreContractSuite.WriteAgainChangesEtag(Create);

    /// <summary>
    /// List sees direct children only; deeper descendants collapse into intermediate collections.
    /// </summary>
    [Fact]
    public Task List_Root_ReturnsDirectChildrenOnly() => RemoteStoreContractSuite.ListRootChildrenOnly(Create);

    /// <summary>
    /// LIST of a missing collection — <see cref="ErrorKind.NotFound"/> (not an empty list).
    /// </summary>
    [Fact]
    public Task List_MissingFolder_NotFound() => RemoteStoreContractSuite.ListMissingFolderNotFound(Create);

    /// <summary>
    /// MKCOL is idempotent: the second call also succeeds.
    /// </summary>
    [Fact]
    public Task CreateFolder_Twice_IsIdempotent() => RemoteStoreContractSuite.CreateFolderTwiceIdempotent(Create);

    /// <summary>
    /// A write into a missing collection is refused (<see cref="ErrorKind.Conflict"/> on the fake):
    /// folders are the plan's own phase, never a side effect of a file write.
    /// </summary>
    [Fact]
    public Task Write_MissingParent_Refused() => RemoteStoreContractSuite.WriteMissingParentRefused(Create);

    /// <summary>
    /// MOVE relocates a subtree; occupied target — <see cref="ErrorKind.Conflict"/>.
    /// </summary>
    [Fact]
    public Task Move_RelocatesSubtree_AndRejectsOccupiedTarget() =>
        RemoteStoreContractSuite.MoveRelocatesSubtree(Create);

    /// <summary>
    /// DELETE removes a subtree; deleting a deep descendant leaves siblings untouched.
    /// </summary>
    [Fact]
    public Task Delete_RemovesSubtree_KeepsSiblings() => RemoteStoreContractSuite.DeleteRemovesSubtree(Create);
}

/// <summary>
/// Contract semantics as pure functions of a store factory — shared by fake and live runs.
/// </summary>
public static class RemoteStoreContractSuite
{
    /// <summary>
    /// Round-trip write with a unicode name; ETag and size on the returned entry.
    /// </summary>
    public static async Task WriteThenReadRoundTrips(Func<IWritableRemoteStore> create)
    {
        var store = create();
        var path = RemotePath.Parse("Docs/héllo wörld.txt");
        var payload = new byte[] { 5, 6, 7 };

        Assert.True((await store.CreateFolderAsync(RemotePath.Parse("Docs"), CancellationToken.None)).IsSuccess);
        var written = await store.WriteAsync(path, new MemoryStream(payload), CancellationToken.None);
        written.AssertOk();
        Assert.Equal(payload.Length, written.Value.Size);
        Assert.NotNull(written.Value.ETag);

        using var read = (await store.ReadAsync(path, CancellationToken.None)).Value;
        using var buffer = new MemoryStream();
        read.CopyTo(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    /// <summary>
    /// Rewriting changes the ETag — otherwise diff and pruning would lie. Servers that derive the
    /// fingerprint from the modification moment answer the old one until that moment advances (one
    /// second on the live backend), so the honest contract is "changes within the next tick":
    /// rewrite until the ETag moves, with a deadline past the coarsest tick. Instant stores — the
    /// in-memory server, the HTTP fake — pass on the first rewrite without a single delay.
    /// </summary>
    public static async Task WriteAgainChangesEtag(Func<IWritableRemoteStore> create)
    {
        var store = create();
        var path = RemotePath.Parse("f.txt");

        var first = (await store.WriteAsync(path, new MemoryStream([1]), CancellationToken.None)).Value;
        var etag = (await store.WriteAsync(path, new MemoryStream([1, 2]), CancellationToken.None)).Value.ETag;

        for (var attempt = 0; attempt < 5 && etag == first.ETag; attempt++)
        {
            await Task.Delay(300, CancellationToken.None);
            etag = (await store.WriteAsync(path, new MemoryStream([1, 2, 3]), CancellationToken.None)).Value.ETag;
        }

        Assert.NotEqual(first.ETag, etag);
    }

    /// <summary>
    /// Depth-1 list: children only, deep content collapses into a collection.
    /// </summary>
    public static async Task ListRootChildrenOnly(Func<IWritableRemoteStore> create)
    {
        var store = create();
        await store.CreateFolderAsync(RemotePath.Parse("sub"), CancellationToken.None);
        await store.CreateFolderAsync(RemotePath.Parse("sub/deep"), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("a.txt"), new MemoryStream([1]), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("sub/deep/b.txt"), new MemoryStream([2]), CancellationToken.None);

        var list = (await store.ListAsync(RemotePath.Root, CancellationToken.None)).Value;
        var byName = list.ToDictionary(static e => e.Path.Name);

        Assert.Equal(EntryKind.File, byName["a.txt"].Kind);
        Assert.Equal(EntryKind.Directory, byName["sub"].Kind);
        Assert.Equal(2, list.Count);
    }

    /// <summary>
    /// Missing collection → NotFound, not an empty list (the scanner distinguishes these states).
    /// </summary>
    public static async Task ListMissingFolderNotFound(Func<IWritableRemoteStore> create)
    {
        var store = create();
        var result = await store.ListAsync(RemotePath.Parse("nope"), CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    /// <summary>
    /// MKCOL twice — both succeed (idempotence).
    /// </summary>
    public static async Task CreateFolderTwiceIdempotent(Func<IWritableRemoteStore> create)
    {
        var store = create();
        var folder = RemotePath.Parse("coll");

        Assert.True((await store.CreateFolderAsync(folder, CancellationToken.None)).IsSuccess);
        Assert.True((await store.CreateFolderAsync(folder, CancellationToken.None)).IsSuccess);
    }

    /// <summary>
    /// MOVE with subtree integrity; an occupied target — Conflict.
    /// </summary>
    public static async Task MoveRelocatesSubtree(Func<IWritableRemoteStore> create)
    {
        var store = create();
        await store.CreateFolderAsync(RemotePath.Parse("src"), CancellationToken.None);
        await store.CreateFolderAsync(RemotePath.Parse("src/deep"), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("src/a.txt"), new MemoryStream([1]), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("src/deep/b.txt"), new MemoryStream([2]), CancellationToken.None);

        Assert.True((await store.MoveAsync(RemotePath.Parse("src"), RemotePath.Parse("dst"), CancellationToken.None)).IsSuccess);
        Assert.True((await store.ReadAsync(RemotePath.Parse("dst/deep/b.txt"), CancellationToken.None)).IsSuccess);
        Assert.True((await store.ReadAsync(RemotePath.Parse("src/a.txt"), CancellationToken.None)).IsFailure);

        // the moved collection itself is gone — a rename leaves no empty shell behind (a folder
        // conflict resolution ends with the old name absent on both lists)
        var names = (await store.ListAsync(RemotePath.Root, CancellationToken.None)).Value
            .Select(static e => e.Path.Name).ToArray();
        Assert.DoesNotContain("src", names);
        Assert.Contains("dst", names);

        await store.WriteAsync(RemotePath.Parse("x"), new MemoryStream([3]), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("y"), new MemoryStream([4]), CancellationToken.None);
        var conflict = await store.MoveAsync(RemotePath.Parse("x"), RemotePath.Parse("y"), CancellationToken.None);
        Assert.True(conflict.IsFailure);
        Assert.Equal(ErrorKind.Conflict, conflict.Error.Kind);
    }

    /// <summary>
    /// DELETE is recursive, siblings stay intact.
    /// </summary>
    public static async Task DeleteRemovesSubtree(Func<IWritableRemoteStore> create)
    {
        var store = create();
        await store.CreateFolderAsync(RemotePath.Parse("dir"), CancellationToken.None);
        await store.CreateFolderAsync(RemotePath.Parse("dir/sub"), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("dir/a.txt"), new MemoryStream([1]), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("dir/sub/b.txt"), new MemoryStream([2]), CancellationToken.None);
        await store.WriteAsync(RemotePath.Parse("sibling.txt"), new MemoryStream([3]), CancellationToken.None);

        Assert.True((await store.DeleteAsync(RemotePath.Parse("dir"), CancellationToken.None)).IsSuccess);
        Assert.True((await store.ReadAsync(RemotePath.Parse("dir/sub/b.txt"), CancellationToken.None)).IsFailure);
        Assert.True((await store.ReadAsync(RemotePath.Parse("sibling.txt"), CancellationToken.None)).IsSuccess);
    }

    /// <summary>
    /// A write into a missing collection is refused without creating anything. Servers answer the
    /// refusal differently (the fake mirrors RFC 4918's 409, the live server responds 404) — the
    /// honest contract is "no file appears", whatever the status.
    /// </summary>
    public static async Task WriteMissingParentRefused(Func<IWritableRemoteStore> create)
    {
        var store = create();

        var result = await store.WriteAsync(RemotePath.Parse("nowhere/file.bin"), new MemoryStream([1]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.True(result.Error.Kind is ErrorKind.Conflict or ErrorKind.NotFound);
        Assert.True((await store.ReadAsync(RemotePath.Parse("nowhere/file.bin"), CancellationToken.None)).IsFailure);
    }
}
