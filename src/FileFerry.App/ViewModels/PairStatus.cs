namespace FileFerry.App.ViewModels;

/// <summary>
/// Pair status for the SyncStatusBadge chip (six states). A presentation projection
/// of the engine's run states; live transitions will be fed by <c>SyncStatusService</c>.
/// </summary>
public enum PairStatus
{
    /// <summary>
    /// Queued / waiting for the scheduled run.
    /// </summary>
    Idle,

    /// <summary>
    /// A run is in progress (animated `sync`).
    /// </summary>
    Syncing,

    /// <summary>
    /// The last run finished successfully.
    /// </summary>
    Success,

    /// <summary>
    /// There are unresolved conflicts.
    /// </summary>
    Conflict,

    /// <summary>
    /// The last run finished with an error.
    /// </summary>
    Error,

    /// <summary>
    /// The server is unreachable.
    /// </summary>
    Offline,
}
