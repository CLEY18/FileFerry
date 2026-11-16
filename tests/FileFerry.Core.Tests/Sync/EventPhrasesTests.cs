using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Sync;
using FileFerry.Core.Tests.Localization;

namespace FileFerry.Core.Tests.Sync;

/// <summary>
/// A journal event is structured (kind + args); text is rendered at display time.
/// Core defines the phrase key and args (no resx dependency); the App renders the
/// key via AppStrings — so every locale must define every key.
/// </summary>
[Trait("Category", "Unit")]
public class EventPhrasesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(EventKind.RunStarted, "EventRunStarted")]
    [InlineData(EventKind.RunCancelled, "EventRunCancelled")]
    [InlineData(EventKind.ResumeStopped, "EventResumeStopped")]
    [InlineData(EventKind.ConflictDetected, "EventConflictDetected")]
    [InlineData(EventKind.PlanSummary, "EventPlanSummary")]
    [InlineData(EventKind.AppliedBaseline, "EventAppliedBaseline")]
    [InlineData(EventKind.RunStopped, "EventRunStopped")]
    [InlineData(EventKind.StashPurged, "EventStashPurged")]
    [InlineData(EventKind.FreeText, "EventRaw")]
    public void Kind_maps_to_its_phrase_key(EventKind kind, string expectedKey)
    {
        var e = new SyncEvent(At, kind);

        Assert.Equal(expectedKey, e.Phrase().Key);
    }

    [Fact]
    public void Count_arg_flows_to_summary_phrases()
    {
        var plan = new SyncEvent(At, EventKind.PlanSummary,
            Args: new Dictionary<string, string> { ["count"] = "4" });
        var applied = new SyncEvent(At, EventKind.AppliedBaseline, SyncEventOutcome.Success,
            Args: new Dictionary<string, string> { ["count"] = "4" });

        Assert.Equal(4, Assert.Single(plan.Phrase().Args));
        Assert.Equal("EventPlanSummary", plan.Phrase().Key);
        Assert.Equal(4, Assert.Single(applied.Phrase().Args));
        Assert.Equal("EventAppliedBaseline", applied.Phrase().Key);
    }

    [Fact]
    public void Conflict_and_failure_args_flow_through()
    {
        var conflict = new SyncEvent(At, EventKind.ConflictDetected, SyncEventOutcome.Warning,
            RelativePath.Parse("a.txt"), new Dictionary<string, string> { ["conflict"] = "AddAdd" });
        var failed = new SyncEvent(At, EventKind.RunFailed, SyncEventOutcome.Error, null,
            new Dictionary<string, string>
            {
                ["label"] = EventPhrases.FailProbe,
                ["detail"] = "HTTP 404",
            });

        Assert.Equal("AddAdd", Assert.Single(conflict.Phrase().Args));
        var (failKey, failArgs) = failed.Phrase();
        Assert.Equal("EventFailProbe", failKey);
        Assert.Equal("HTTP 404", Assert.Single(failArgs));
    }

    [Fact]
    public void FreeText_renders_its_detail_verbatim()
    {
        var e = new SyncEvent(At, EventKind.FreeText, Args: new Dictionary<string, string> { ["detail"] = "MKCOL 507" });

        var (key, args) = e.Phrase();

        Assert.Equal("EventRaw", key);
        Assert.Equal("MKCOL 507", Assert.Single(args));
    }

    [Fact]
    public void Unknown_and_unlabeled_fallbacks_stay_visible_not_fatal()
    {
        // a new kind from a newer Core, read by an older App — shows the key name, does not crash
        var future = new SyncEvent(At, (EventKind)999, Args: new Dictionary<string, string> { ["detail"] = "x" });
        var oddLabel = new SyncEvent(At, EventKind.RunFailed, SyncEventOutcome.Error, null,
            new Dictionary<string, string> { ["label"] = "Martian", ["detail"] = "x" });

        var (key, args) = future.Phrase();
        Assert.Equal("Event999", key); // "Event" + kind name: a version mismatch stays visible
        Assert.Equal("x", Assert.Single(args));
        Assert.Equal("EventRunFailed", EventPhrases.FailPhrase("Martian"));
        Assert.Equal("EventRunFailed", oddLabel.Phrase().Key);
    }

    [Fact]
    public void Every_phrase_key_exists_in_every_locale()
    {
        var phrasesSource = File.ReadAllText(
            Path.Combine(AppStringsFiles.Root, "src", "FileFerry.Core", "Sync", "EventPhrases.cs"));
        var keys = System.Text.RegularExpressions.Regex
            .Matches(phrasesSource, "\"(Event[A-Za-z]+)\"")
            .Select(static m => m.Groups[1].Value)
            .Where(static k => k != "EventFail") // concatenation prefix, not a standalone resource
            .ToHashSet();
        foreach (var label in EventPhrases.FailLabels)
        {
            keys.Add(EventPhrases.FailPhrase(label));
        }

        Assert.NotEmpty(keys);
        foreach (var entries in AppStringsFiles.AllLocales().Values)
        {
            foreach (var key in keys)
            {
                Assert.Contains(key, entries.Keys);
            }
        }
    }
}
