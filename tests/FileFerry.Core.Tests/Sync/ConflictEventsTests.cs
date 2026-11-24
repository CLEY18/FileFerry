using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Sync;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// The one counting rule every screen reads its conflict badge from: a Warning event with a
/// path is a conflict row; a keep-side resolution of the same path acknowledges it. Diverging
/// counters across screens make a resolved conflict look open — these tests pin the rule whole.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConflictEventsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void WarningWithAPath_Counts_ServiceWarningDoesNot()
    {
        var runs = Runs(
            Conflict("docs/a.txt", at: T0.AddMinutes(1)),
            new SyncEvent(T0.AddMinutes(2), EventKind.StashPurged, SyncEventOutcome.Warning)); // service warning, no path

        Assert.Equal(1, ConflictEvents.CountUnresolved(runs));
    }

    [Fact]
    public void LaterResolution_StopsTheCount_LaterConflict_ReopensIt()
    {
        var runs = Runs(
            Conflict("docs/a.txt", at: T0.AddMinutes(1)),
            Resolve("docs/a.txt", at: T0.AddMinutes(2)));
        Assert.Equal(0, ConflictEvents.CountUnresolved(runs));

        // the same path conflicts again after the resolution — the new Warning is open
        var reopened = Runs(
            Conflict("docs/a.txt", at: T0.AddMinutes(1)),
            Resolve("docs/a.txt", at: T0.AddMinutes(2)),
            Conflict("docs/a.txt", at: T0.AddMinutes(3)));
        Assert.Equal(1, ConflictEvents.CountUnresolved(reopened));
    }

    [Fact]
    public void Resolution_ForAnotherPath_LeavesTheConflictOpen()
    {
        var runs = Runs(
            Conflict("docs/a.txt", at: T0.AddMinutes(1)),
            Resolve("photos/b.jpg", at: T0.AddMinutes(2)));

        Assert.Equal(1, ConflictEvents.CountUnresolved(runs));
    }

    [Fact]
    public void Events_SpreadOverRuns_StillShareOneRule()
    {
        // the resolution lands in a newer run than the conflict — the window is folded as one
        var first = new SyncRunLog(
            Guid.NewGuid(), Guid.NewGuid(), T0, T0.AddMinutes(1), SyncRunOutcome.Partial,
            [Conflict("docs/a.txt", at: T0.AddMinutes(1)), Conflict("docs/b.txt", at: T0.AddMinutes(1))]);
        var second = new SyncRunLog(
            Guid.NewGuid(), first.PairId, T0.AddMinutes(5), T0.AddMinutes(6), SyncRunOutcome.Success,
            [Resolve("docs/a.txt", at: T0.AddMinutes(5))]);

        Assert.Equal(1, ConflictEvents.CountUnresolved([first, second]));
    }

    private static IReadOnlyList<SyncRunLog> Runs(params SyncEvent[] events) =>
        [new SyncRunLog(Guid.NewGuid(), Guid.NewGuid(), T0, T0.AddMinutes(9), SyncRunOutcome.Partial, events)];

    private static SyncEvent Conflict(string path, DateTimeOffset at) =>
        new(at, EventKind.ConflictDetected, SyncEventOutcome.Warning, RelativePath.Parse(path));

    private static SyncEvent Resolve(string path, DateTimeOffset at) =>
        new(at, EventKind.ConflictResolved, SyncEventOutcome.Info, RelativePath.Parse(path));
}
