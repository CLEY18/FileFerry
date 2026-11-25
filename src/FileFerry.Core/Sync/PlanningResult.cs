using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Conflict kind for the conflicts screen (a conflict is not an "error").
/// </summary>
public enum ConflictKind
{
    /// <summary>
    /// Both sides changed the content differently.
    /// </summary>
    BothModified,

    /// <summary>
    /// One side deleted, the other modified — only one version is alive.
    /// </summary>
    DeleteVersusModify,

    /// <summary>
    /// Two added records differ only by case — they can't coexist on a case-sensitive side.
    /// </summary>
    CaseCollision,
}

/// <summary>
/// Companion record of a conflict: the planner already kept both versions, the UI only informs.
/// </summary>
/// <param name="Kind">The conflict class (drives the conflicts screen wording).</param>
/// <param name="Path">Primary path of the conflicting record.</param>
/// <param name="Folder">A folder is involved: no conflict copies exist for this note, keep-side
/// actions could only fail — the row reads as hand-resolvable (one side renamed by hand).</param>
public sealed record ConflictNote(ConflictKind Kind, RelativePath Path, bool Folder = false);

/// <summary>
/// Planner output: ordered operations + conflict annotations. Timestamped journal events are
/// built by SyncRun — the planner stays pure.
/// </summary>
/// <param name="Operations">Operations in execution order (topology: mkcol↑, move chains, delete↓).</param>
/// <param name="Conflicts">Conflict annotations for the UI/journal.</param>
public sealed record PlanningResult(
    IReadOnlyList<SyncOperation> Operations,
    IReadOnlyList<ConflictNote> Conflicts);
