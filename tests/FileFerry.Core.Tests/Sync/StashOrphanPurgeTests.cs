using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Orphan purge — the pass that keeps the delete dialog's promise: when a pair is deleted,
/// its buffer copies stay retrievable until the TTL ends, then this pass collects them.
/// The rows outlive the pair (with the tree they live in), the copies follow the same
/// rules as a live pair's: inside the buffer only, expired only, a lost grant leaves the
/// row for the next boot, a refusal leaves everything for the next pass.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StashOrphanPurgeTests
{
    private static readonly RelativePath Trash = RelativePath.Parse("FileFerry Trash");

    private readonly MutableClock _clock = new();
    private readonly StashPolicy _policy = new(new StashPolicyOptions());
    private readonly InMemoryStashRepository _index = new();
    private readonly InMemoryFileSystem _treeA;
    private readonly Guid _deadPair = Guid.NewGuid();

    public StashOrphanPurgeTests()
    {
        _treeA = new InMemoryFileSystem(_clock);
    }

    [Fact]
    public async Task ExpiredOrphan_GrantAlive_CopyAndRowGone()
    {
        await SeedCopyAsync("a.txt", minutesOld: _policy.Options.Ttl.TotalMinutes + 1);

        var purged = await StashPurger.PurgeOrphansAsync(
            await _index.ListOrphansAsync(CancellationToken.None),
            key => key == "tree-a" ? _treeA : null,
            _index, _policy, _clock, CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.DoesNotContain((await _treeA.SnapshotAsync(CancellationToken.None)).Value,
            static s => s.Path.StartsWith(Trash) && !s.IsDirectory); // the copy is gone (an empty buffer folder is none of our business)
        Assert.Empty(await _index.ListOrphansAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LiveOrphan_StaysRetrievable()
    {
        await SeedCopyAsync("a.txt", minutesOld: 1);

        var purged = await StashPurger.PurgeOrphansAsync(
            await _index.ListOrphansAsync(CancellationToken.None),
            key => key == "tree-a" ? _treeA : null,
            _index, _policy, _clock, CancellationToken.None);

        Assert.Equal(0, purged);
        Assert.True((await _treeA.ExistsAsync(RelativePath.Parse("FileFerry Trash/a.txt"), CancellationToken.None)).Value);
    }

    [Fact]
    public async Task LostGrant_KeepsRowForTheNextBoot()
    {
        await SeedCopyAsync("a.txt", minutesOld: _policy.Options.Ttl.TotalMinutes + 1);

        var purged = await StashPurger.PurgeOrphansAsync(
            await _index.ListOrphansAsync(CancellationToken.None),
            key => null, // the SAF grant is gone — nobody can reach the tree
            _index, _policy, _clock, CancellationToken.None);

        Assert.Equal(0, purged);
        var orphans = await _index.ListOrphansAsync(CancellationToken.None);
        Assert.Single(orphans); // the row survives: a later boot (or a re-grant) finishes the job
    }

    [Fact]
    public async Task CopyAlreadyGone_HealsTheRow()
    {
        await SeedCopyAsync("a.txt", minutesOld: _policy.Options.Ttl.TotalMinutes + 1);
        await _treeA.DeleteAsync(RelativePath.Parse("FileFerry Trash/a.txt"), CancellationToken.None);

        var purged = await StashPurger.PurgeOrphansAsync(
            await _index.ListOrphansAsync(CancellationToken.None),
            key => key == "tree-a" ? _treeA : null,
            _index, _policy, _clock, CancellationToken.None);

        Assert.Equal(1, purged); // missing file is still "collected" — the index is healed either way
        Assert.Empty(await _index.ListOrphansAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RowOutsideTheBuffer_IsNotTouched()
    {
        await _index.AddAsync(
            new StashEntry(_deadPair, RelativePath.Parse("notes.txt"), RelativePath.Parse("notes.txt"),
                _clock.UtcNow.AddHours(-999), "tree-a"),
            CancellationToken.None);
        _index.RetirePair(_deadPair);
        await _treeA.WriteAsync(RelativePath.Parse("notes.txt"), new MemoryStream(Encoding.UTF8.GetBytes("MINE")), CancellationToken.None);

        var purged = await StashPurger.PurgeOrphansAsync(
            await _index.ListOrphansAsync(CancellationToken.None),
            key => key == "tree-a" ? _treeA : null,
            _index, _policy, _clock, CancellationToken.None);

        Assert.Equal(0, purged);
        Assert.True((await _treeA.ExistsAsync(RelativePath.Parse("notes.txt"), CancellationToken.None)).Value);
    }

    private async Task SeedCopyAsync(string name, double minutesOld)
    {
        var copy = Trash.Combine(name);
        await _treeA.WriteAsync(copy, new MemoryStream(Encoding.UTF8.GetBytes("COPY")), CancellationToken.None);
        await _index.AddAsync(
            new StashEntry(_deadPair, copy, RelativePath.Parse(name), _clock.UtcNow.AddMinutes(-minutesOld), "tree-a"),
            CancellationToken.None);
        _index.RetirePair(_deadPair);
    }
}
