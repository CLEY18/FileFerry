using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// Restoring a buffer copy is an overwrite write, so it obeys the deletion rule: nothing
/// disappears without a copy. The origin path may be busy when the user presses Restore — the
/// server re-delivered the name on a later run, or the user recreated it by hand while the copy
/// waited in the trash — then the current content goes into the buffer before the restore lands.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StashRestoreTests
{
    private readonly MutableClock _clock = new();
    private readonly InMemoryFileSystem _local;
    private readonly StashPolicy _stash = new(new StashPolicyOptions());

    public StashRestoreTests() => _local = new InMemoryFileSystem(_clock);

    [Fact]
    public async Task BusyOrigin_CurrentContentGoesToTheBufferFirst()
    {
        var stored = StampNow() + "/docs/a.txt"; // the copy the user is restoring
        await SeedAsync($"FileFerry Trash/{stored}", "OLD");
        await SeedAsync("docs/a.txt", "NEW"); // refilled after the deletion — the busy origin

        _clock.Advance(TimeSpan.FromMinutes(2));
        var restored = await RestoreAsync(stored, "docs/a.txt");

        Assert.True(restored.IsSuccess, restored.IsFailure ? restored.Error.Message : string.Empty);
        Assert.Equal("OLD", await ReadAsync("docs/a.txt"));
        var safety = _stash.StashDestination(RelativePath.Parse("docs/a.txt"), _clock.UtcNow);
        Assert.Equal("NEW", await ReadAsync(safety.ToString())); // buffered under a fresh stamp, not overwritten
    }

    [Fact]
    public async Task FreeOrigin_LandsWithoutAnExtraCopy()
    {
        var stored = StampNow() + "/b.txt";
        await SeedAsync($"FileFerry Trash/{stored}", "OLD");

        var restored = await RestoreAsync(stored, "b.txt");

        Assert.True(restored.IsSuccess, restored.IsFailure ? restored.Error.Message : string.Empty);
        Assert.Equal("OLD", await ReadAsync("b.txt"));
        var snapshots = await _local.SnapshotAsync(CancellationToken.None);
        Assert.Single(snapshots.Value, static s =>
            !s.IsDirectory && s.Path.StartsWith(RelativePath.Parse("FileFerry Trash")));
    }

    [Fact]
    public async Task BufferingBlocked_LeavesTheBusyOriginUntouched()
    {
        var stored = StampNow() + "/c.txt";
        await SeedAsync($"FileFerry Trash/{stored}", "OLD");
        await SeedAsync("c.txt", "NEW");

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _local.CreateFolderAsync(
            _stash.StashDestination(RelativePath.Parse("c.txt"), _clock.UtcNow), CancellationToken.None);
        // the safety-copy slot is occupied by a directory — the provider refuses the copy

        var restored = await RestoreAsync(stored, "c.txt");

        Assert.True(restored.IsFailure);
        Assert.Equal("NEW", await ReadAsync("c.txt")); // no restore, no loss — the copy waits for a retry
    }

    [Fact]
    public async Task CopyVanishedBetweenListAndTap_FailsHonest_OriginStaysGone()
    {
        // the TTL purge removed the copy after the screen listed it — a NotFound step, not a crash
        var restored = await RestoreAsync("gone/d.txt", "d.txt");

        Assert.True(restored.IsFailure);
        Assert.Equal(ErrorKind.NotFound, restored.Error.Kind);
        var exists = await _local.ExistsAsync(RelativePath.Parse("d.txt"), CancellationToken.None);
        Assert.False(exists.Value);
    }

    private Task<Result<Ok, SyncError>> RestoreAsync(string stashPath, string originPath) =>
        StashRestore.RestoreAsync(
            _local, _stash, _clock,
            RelativePath.Parse($"FileFerry Trash/{stashPath}"), RelativePath.Parse(originPath),
            CancellationToken.None);

    private string StampNow() => _clock.UtcNow.ToString("yyyyMMdd'T'HHmmss");

    private async Task SeedAsync(string path, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var written = await _local.WriteAsync(RelativePath.Parse(path), stream, CancellationToken.None);
        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : string.Empty);
    }

    private async Task<string> ReadAsync(string path)
    {
        var opened = await _local.ReadAsync(RelativePath.Parse(path), CancellationToken.None);
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : string.Empty);
        await using (opened.Value)
        using (var reader = new StreamReader(opened.Value))
        {
            return await reader.ReadToEndAsync();
        }
    }
}
