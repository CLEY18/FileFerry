using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.ContractTests;

/// <summary>
/// Contract suite for ALL <see cref="ILocalFileSystem"/> implementations: inherit the
/// class, provide a factory — and the same semantics is verified on the fake, plain-FS and SAF (L).
/// </summary>
[Trait("Category", "Contract")]
public abstract class LocalFileSystemContract
{
    /// <summary>
    /// A clean (empty) FS for each test — no state carried between cases.
    /// </summary>
    protected abstract ILocalFileSystem Create();

    /// <summary>
    /// Write and read — the same content round-trips.
    /// </summary>
    [Fact]
    public async Task Write_ThenRead_RoundTrips()
    {
        var fs = Create();
        var path = RelativePath.Parse("Docs/a b.txt");
        var payload = new byte[] { 1, 2, 3, 4 };

        var written = await fs.WriteAsync(path, new MemoryStream(payload), CancellationToken.None);
        written.AssertOk();
        Assert.Equal(payload.Length, written.Value.Size);

        var read = await fs.ReadAsync(path, CancellationToken.None);
        Assert.True(read.IsSuccess);
        using var stream = read.Value;
        Assert.Equal(payload, ToArray(stream));
    }

    /// <summary>
    /// The snapshot sees both the file and the (auto)created ancestor directories.
    /// </summary>
    [Fact]
    public async Task Snapshot_ListsFilesAndAncestorFolders()
    {
        var fs = Create();
        await fs.WriteAsync(RelativePath.Parse("a/b/c.txt"), new MemoryStream([9]), CancellationToken.None);

        var snapshot = await fs.SnapshotAsync(CancellationToken.None);
        Assert.True(snapshot.IsSuccess);

        var byPath = snapshot.Value.ToDictionary(static s => s.Path.ToString());
        Assert.Equal(EntryKind.File, byPath["a/b/c.txt"].Kind);
        Assert.Equal(EntryKind.Directory, byPath["a/b"].Kind);
    }

    /// <summary>
    /// Regression: the write snapshot must agree with <c>SnapshotAsync</c>
    /// in the evidence (size + mtime at second granularity) — otherwise the baseline gets an
    /// mtime the scan will never see and the file re-downloads every morning (endless churn).
    /// </summary>
    [Fact]
    public async Task Write_SnapshotAgreesWithScan_Evidence()
    {
        var fs = Create();
        var path = RelativePath.Parse("churn.txt");

        var written = await fs.WriteAsync(path, new MemoryStream([1, 2, 3, 4, 5]), CancellationToken.None);
        written.AssertOk();

        var scan = await fs.SnapshotAsync(CancellationToken.None);
        scan.AssertOk();
        var seen = Assert.Single(scan.Value, s => s.Path.Equals(path));

        Assert.Equal(written.Value.Size, seen.Size);
        if (written.Value.ModifiedAt is not null && seen.ModifiedAt is not null)
        {
            var drift = (written.Value.ModifiedAt - seen.ModifiedAt).Value;
            Assert.True(drift <= TimeSpan.FromSeconds(1) && drift >= TimeSpan.FromSeconds(-1),
                $"mtime from write ({written.Value.ModifiedAt}) and from scan ({seen.ModifiedAt}) differ by more than a second");
        }
    }

    /// <summary>
    /// Moving a local file — the source disappears, the target appears (identity, not a copy).
    /// </summary>
    [Fact]
    public async Task Move_RenamesFile()
    {
        var fs = Create();
        var from = RelativePath.Parse("old.txt");
        var to = RelativePath.Parse("nested/new.txt");
        await fs.WriteAsync(from, new MemoryStream([1]), CancellationToken.None);

        var moved = await fs.MoveAsync(from, to, CancellationToken.None);
        moved.AssertOk();
        Assert.True((await fs.ReadAsync(to, CancellationToken.None)).IsSuccess);
        var gone = await fs.ReadAsync(from, CancellationToken.None);
        Assert.True(gone.IsFailure);
        Assert.Equal(ErrorKind.NotFound, gone.Error.Kind);
    }

    /// <summary>
    /// Moving a folder relocates the whole subtree.
    /// </summary>
    [Fact]
    public async Task Move_OfFolder_RelocatesSubtree()
    {
        var fs = Create();
        await fs.WriteAsync(RelativePath.Parse("src/a.txt"), new MemoryStream([1]), CancellationToken.None);
        await fs.WriteAsync(RelativePath.Parse("src/deep/b.txt"), new MemoryStream([2]), CancellationToken.None);

        Assert.True((await fs.MoveAsync(RelativePath.Parse("src"), RelativePath.Parse("dst"), CancellationToken.None)).IsSuccess);

        var snapshot = (await fs.SnapshotAsync(CancellationToken.None)).Value;
        Assert.DoesNotContain("src/a.txt", snapshot.Select(static s => s.Path.ToString()));
        Assert.Contains("dst/deep/b.txt", snapshot.Select(static s => s.Path.ToString()));
    }

    /// <summary>
    /// Delete removes the entry; a second read yields <see cref="ErrorKind.NotFound"/>.
    /// </summary>
    [Fact]
    public async Task Delete_RemovesFile()
    {
        var fs = Create();
        var path = RelativePath.Parse("todelete.bin");
        await fs.WriteAsync(path, new MemoryStream([0]), CancellationToken.None);

        Assert.True((await fs.DeleteAsync(path, CancellationToken.None)).IsSuccess);
        var missing = await fs.ReadAsync(path, CancellationToken.None);
        Assert.True(missing.IsFailure);
        Assert.Equal(ErrorKind.NotFound, missing.Error.Kind);
    }

    /// <summary>
    /// Copy keeps the source (stash semantics: the original stays untouched until deletion).
    /// </summary>
    [Fact]
    public async Task Copy_KeepsSource()
    {
        var fs = Create();
        var from = RelativePath.Parse("file.txt");
        await fs.WriteAsync(from, new MemoryStream([7, 7]), CancellationToken.None);

        Assert.True((await fs.CopyAsync(from, RelativePath.Parse("FileFerry Trash/file.txt"), CancellationToken.None)).IsSuccess);
        Assert.True((await fs.ReadAsync(from, CancellationToken.None)).IsSuccess);
    }

    /// <summary>
    /// Moving onto an occupied target — <see cref="ErrorKind.Conflict"/> (no silent overwrite).
    /// </summary>
    [Fact]
    public async Task Move_OccupiedTarget_Conflicts()
    {
        var fs = Create();
        await fs.WriteAsync(RelativePath.Parse("a.txt"), new MemoryStream([1]), CancellationToken.None);
        await fs.WriteAsync(RelativePath.Parse("b.txt"), new MemoryStream([2]), CancellationToken.None);

        var moved = await fs.MoveAsync(RelativePath.Parse("a.txt"), RelativePath.Parse("b.txt"), CancellationToken.None);
        Assert.True(moved.IsFailure);
        Assert.Equal(ErrorKind.Conflict, moved.Error.Kind);
    }

    private static byte[] ToArray(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
