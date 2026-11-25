using FileFerry.Core.Domain;

namespace FileFerry.Core.Sync;

/// <summary>
/// Journal event → phrase key + arguments for rendering in the App (AppStrings).
/// Core knows no current-language text — only intent; the App resolves the resx string by key,
/// so the journal repaints together with the interface. <see cref="EventKind.FreeText"/>
/// renders its <c>detail</c> as-is; an unknown (future) kind renders as the visible key name
/// instead of panicking. The <c>Event*</c> keys here must exist in every locale of AppStrings —
/// covered by a test.
/// </summary>
public static class EventPhrases
{
    /// <summary>
    /// Failure-stage keys for the <c>label</c> argument (part of the "EventFail{label}" phrase key).
    /// </summary>
    public const string FailScope = "Scope",
                       FailProbe = "Probe",
                       FailMaintenance = "Maintenance",
                       FailLocalScan = "LocalScan",
                       FailRemoteScan = "RemoteScan",
                       FailPlanner = "Planner",
                       FailPipeline = "Pipeline";

    /// <summary>
    /// Known failure stages (for key generation and resx-coverage checks).
    /// </summary>
    public static IReadOnlyList<string> FailLabels { get; } =
        [FailScope, FailProbe, FailMaintenance, FailLocalScan, FailRemoteScan, FailPlanner, FailPipeline];

    /// <summary>
    /// A stage collapsed: phrase key from the <c>label</c> (unknown → generic EventRunFailed).
    /// </summary>
    public static string FailPhrase(string label) =>
        FailLabels.Contains(label) ? "EventFail" + label : "EventRunFailed";

    /// <summary>
    /// The phrase resource key and <c>string.Format</c> arguments for an event.
    /// </summary>
    public static (string Key, object?[] Args) Phrase(this SyncEvent e) => e.Kind switch
    {
        EventKind.RunStarted => ("EventRunStarted", []),
        EventKind.RunCancelled => ("EventRunCancelled", []),
        EventKind.ResumeStopped => ("EventResumeStopped", [Count(e)]),
        EventKind.ConflictDetected => ("EventConflictDetected", [Arg(e, "conflict")]),
        EventKind.PlanSummary => ("EventPlanSummary", [Count(e)]),
        EventKind.AppliedBaseline => ("EventAppliedBaseline", [Count(e)]),
        EventKind.RunStopped => ("EventRunStopped", [Arg(e, "detail")]),
        EventKind.RunFailed => (FailPhrase(Arg(e, "label")), [Arg(e, "detail")]),
        EventKind.StashPurged => ("EventStashPurged", [Count(e)]),
        EventKind.ConflictResolved => (ResolvePhrase(Arg(e, "side"), Arg(e, "action")), []),
        // free text — its detail is the phrase; an unknown (future) kind — the visible key name
        EventKind.FreeText => ("EventRaw", [Arg(e, "detail")]),
        _ => ("Event" + e.Kind, [Arg(e, "detail")]),
    };

    /// <summary>
    /// Resolution phrases: the folder-action phrases first, then the kept side of a file
    /// resolution (an unknown side → the neutral "resolved" phrase).
    /// </summary>
    public static string ResolvePhrase(string side, string action = "") => action switch
    {
        "rename" => side == "local" ? "EventResolvedFolderRenamedLocal" : "EventResolvedFolderRenamedServer",
        "delete" => side == "local" ? "EventResolvedFolderDeletedLocal" : "EventResolvedFolderDeletedServer",
        _ => side switch
        {
            "local" => "EventResolvedLocal",
            "server" => "EventResolvedServer",
            _ => "EventConflictResolved",
        },
    };

    private static string Arg(SyncEvent e, string name) =>
        e.Args is not null && e.Args.TryGetValue(name, out var raw) ? raw : string.Empty;

    private static object Count(SyncEvent e) =>
        int.TryParse(Arg(e, "count"), out var n) ? n : 0;
}
