using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Domain;

/// <summary>
/// What changed relative to the baseline on one or both sides (DiffEngine output — a
/// pure function). The side (local/remote/both) and the evidence are carried
/// by the snapshots themselves inside <see cref="Change"/>.
/// </summary>
public enum ChangeKind
{
    /// <summary>
    /// Appeared where the baseline had nothing.
    /// </summary>
    Added,

    /// <summary>
    /// Content/properties changed for an entry present in the baseline.
    /// </summary>
    Modified,

    /// <summary>
    /// Disappeared where the baseline had an entry.
    /// </summary>
    Deleted,

    /// <summary>
    /// Rename/move: a delete+add pair with identical evidence.
    /// </summary>
    Moved,

    /// <summary>
    /// Both sides changed incompatibly — input for the conflict strategy.
    /// </summary>
    Both,

    /// <summary>
    /// Both sides added the entry and it is provably shared (hash/etag for files;
    /// directories by definition), but no baseline row exists — input for RecordConvergence.
    /// </summary>
    Converged,
}

/// <summary>
/// One recorded entry change for the planner. Snapshots hold "before/after";
/// <see cref="OldPath"/> is populated only for <see cref="ChangeKind.Moved"/>.
/// </summary>
/// <param name="Kind">Change class against the baseline; drives the planner's branching.</param>
/// <param name="Path">"After" path (for Deleted — the path that disappeared).</param>
/// <param name="Base">Baseline snapshot (<see langword="null"/> if the entry wasn't there).</param>
/// <param name="Local">Current local snapshot (<see langword="null"/> — absent locally).</param>
/// <param name="Remote">Current remote snapshot (<see langword="null"/> — absent on the server).</param>
/// <param name="OldPath">Previous path for Moved candidates.</param>
public sealed record Change(
    ChangeKind Kind,
    RelativePath Path,
    FileSnapshot? Base = null,
    FileSnapshot? Local = null,
    FileSnapshot? Remote = null,
    RelativePath? OldPath = null)
{
    /// <summary>
    /// Whether evidence of change exists on both sides at once (conflict candidate).
    /// </summary>
    public bool IsTwoSided => Kind == ChangeKind.Both;
}
