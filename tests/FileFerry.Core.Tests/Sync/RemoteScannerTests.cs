using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// <see cref="RemoteScanner"/> (its Core-side share): a missing root ≡ an empty tree,
/// a subtree hole stops the run, ETag pruning never descends into an unchanged collection.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RemoteScannerTests
{
    private static readonly RemotePath Root = RemotePath.Parse("sync");

    [Fact]
    public async Task MissingRoot_YieldsEmptyTree()
    {
        var scan = await RemoteScanner.ScanAsync(new InMemoryRemoteStore(), Root, [], CancellationToken.None);

        Assert.True(scan.IsSuccess);
        Assert.Empty(scan.Value);
    }

    [Fact]
    public async Task SubtreeListingFailure_StopsScan()
    {
        var remote = new InMemoryRemoteStore();
        await remote.WriteAsync(RemotePath.Parse("sync/sub/x.txt"), new MemoryStream([1]), CancellationToken.None);
        var broken = new CountingBrowser(remote) { FailOn = RemotePath.Parse("sync/sub") };

        var scan = await RemoteScanner.ScanAsync(broken, Root, [], CancellationToken.None);

        Assert.True(scan.IsFailure);
        Assert.Equal(ErrorKind.NotFound, scan.Error.Kind); // a partial scan failure — no run
    }

    [Fact]
    public async Task Pruning_SkipsUnchangedCollection_AndSplicesBaselineSubtree()
    {
        var remote = new InMemoryRemoteStore();
        await remote.WriteAsync(RemotePath.Parse("sync/keep/x.txt"), new MemoryStream([1]), CancellationToken.None);
        await remote.WriteAsync(RemotePath.Parse("sync/fresh/y.txt"), new MemoryStream([2]), CancellationToken.None);

        var rootList = (await remote.ListAsync(Root, CancellationToken.None)).Value;
        var keepEtag = rootList.Single(static e => e.Path.Name == "keep").ETag!;
        var baseline = new[]
        {
            new FileSnapshot(RelativePath.Parse("keep"), EntryKind.Directory, 0, ETag: keepEtag),
            new FileSnapshot(RelativePath.Parse("keep/x.txt"), EntryKind.File, 1, ETag: "\"x1\""),
        };

        var counter = new CountingBrowser(remote);
        var scan = await RemoteScanner.ScanAsync(counter, Root, baseline, CancellationToken.None);

        Assert.True(scan.IsSuccess);
        Assert.DoesNotContain("sync/keep", counter.Listed); // the subtree was not descended into
        var paths = scan.Value.Select(static s => s.Path.ToString()).ToArray();
        Assert.Contains("keep", paths);
        Assert.Contains("keep/x.txt", paths); // the baseline became the head of the "frozen" subtree
        Assert.Contains("fresh/y.txt", paths);
        Assert.Single(scan.Value, static s => s.Path.ToString() == "keep"); // no duplicate directory entry
    }

    [Fact]
    public async Task Pruning_Descends_WhenCollectionEtagChanged()
    {
        var remote = new InMemoryRemoteStore();
        await remote.WriteAsync(RemotePath.Parse("sync/keep/x.txt"), new MemoryStream([1]), CancellationToken.None);

        var baseline = new[]
        {
            new FileSnapshot(RelativePath.Parse("keep"), EntryKind.Directory, 0, ETag: "\"stale\""),
            new FileSnapshot(RelativePath.Parse("keep/x.txt"), EntryKind.File, 1, ETag: "\"x1\""),
        };

        var counter = new CountingBrowser(remote);
        var scan = await RemoteScanner.ScanAsync(counter, Root, baseline, CancellationToken.None);

        Assert.True(scan.IsSuccess);
        Assert.Contains("sync/keep", counter.Listed); // no evidence for pruning — keep scanning
        Assert.Contains(scan.Value, static s =>
            s.Path.ToString() == "keep/x.txt" && s.ETag != "\"x1\""); // fresh server ETag, not the baseline one
    }

    [Fact]
    public async Task BaselineWithoutEtags_NeverPrunes()
    {
        var remote = new InMemoryRemoteStore();
        await remote.WriteAsync(RemotePath.Parse("sync/keep/x.txt"), new MemoryStream([1]), CancellationToken.None);
        var baseline = new[] { new FileSnapshot(RelativePath.Parse("keep"), EntryKind.Directory, 0) };

        var counter = new CountingBrowser(remote);
        var scan = await RemoteScanner.ScanAsync(counter, Root, baseline, CancellationToken.None);

        Assert.True(scan.IsSuccess);
        Assert.Contains("sync/keep", counter.Listed);
    }

    /// <summary>
    /// Browser wrapper: logs visited folders and can emulate a hole in one of them.
    /// </summary>
    private sealed class CountingBrowser(IRemoteBrowser inner) : IRemoteBrowser
    {
        /// <summary>
        /// Visited folders in scan order.
        /// </summary>
        public List<string> Listed { get; } = [];

        /// <summary>
        /// The folder whose listing is force-failed with NotFound.
        /// </summary>
        public RemotePath? FailOn { get; init; }

        public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct)
        {
            Listed.Add(folder.ToString());
            if (FailOn is { } fail && folder.Equals(fail))
            {
                return Task.FromResult(Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(
                    SyncError.Of(ErrorKind.NotFound, $"Simulated hole at '{folder}'.")));
            }

            return inner.ListAsync(folder, ct);
        }

        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) => inner.ReadAsync(file, ct);
    }
}
