using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Domain;

/// <summary>
/// Atomic pipeline action executed by <c>OperationExecutor</c> (the single place with IO).
/// Each kind has its own <c>IOperationHandler</c> in the executor's registry (OCP).
/// </summary>
public enum OperationKind
{
    /// <summary>
    /// MKCOL on the server (before any PUT inside it — topological order).
    /// </summary>
    CreateRemoteFolder,

    /// <summary>
    /// Create a directory locally (MKDIR).
    /// </summary>
    CreateLocalFolder,

    /// <summary>
    /// Local file → server (PUT / chunked).
    /// </summary>
    UploadToRemote,

    /// <summary>
    /// Server file → local (GET + tmp + rename).
    /// </summary>
    DownloadToLocal,

    /// <summary>
    /// MOVE on the server (result of rename-detection).
    /// </summary>
    MoveRemote,

    /// <summary>
    /// Rename locally.
    /// </summary>
    MoveLocal,

    /// <summary>
    /// Local copy inside the tree (conflict copy "(conflicted copy)"; the original stays).
    /// </summary>
    CopyLocal,

    /// <summary>
    /// Delete from the server — only after a copy in the stash (invariant).
    /// </summary>
    DeleteRemote,

    /// <summary>
    /// Delete locally — only after a copy in the stash (invariant).
    /// </summary>
    DeleteLocal,

    /// <summary>
    /// Convergence no-op: a state provably shared by both sides with no baseline row.
    /// No touch to either side; "applied" through the journal to legally seed the baseline
    /// row (only actually applied operations). Without it a file "resurrects" after deletion.
    /// </summary>
    RecordConvergence,
}

/// <summary>
/// A planned run operation: what, where, and with what evidence. Immutable record;
/// execution changes only the journal.
/// </summary>
/// <param name="Kind">Atomic action type; selects the executor's handler.</param>
/// <param name="Path">Target "after" path (for Moved — the new path; for uploads — the local target).</param>
/// <param name="SourcePath">Source: for Move — where; for Upload/Download — the path on the opposite side
/// when it differs from <paramref name="Path"/> (conflict copy, stash); otherwise <see langword="null"/>.</param>
/// <param name="After">State snapshot the operation should end with (for the baseline update).</param>
public sealed record SyncOperation(
    OperationKind Kind,
    RelativePath Path,
    RelativePath? SourcePath = null,
    FileSnapshot? After = null)
{
    /// <summary>
    /// Whether this is a delete — the executor checks the "stash first" requirement via this flag.
    /// </summary>
    public bool IsDelete => Kind is OperationKind.DeleteLocal or OperationKind.DeleteRemote;
}
