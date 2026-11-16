namespace FileFerry.Core.Domain;

/// <summary>
/// Conflict resolution strategy (Strategy); v1 supports KeepBoth only.
/// </summary>
public enum ConflictStrategy
{
    /// <summary>
    /// Both versions stay: the loser gets the name "name (conflicted copy).ext".
    /// </summary>
    KeepBoth,
}

/// <summary>
/// Background sync schedule for a pair (WorkManager period; Manual — UI button only).
/// </summary>
public enum SyncSchedule
{
    /// <summary>
    /// Manual only (no runs are scheduled).
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Every 15 minutes (WorkManager lower bound, default).
    /// </summary>
    Every15Minutes = 15,

    /// <summary>
    /// Every 30 minutes.
    /// </summary>
    Every30Minutes = 30,

    /// <summary>
    /// Hourly.
    /// </summary>
    Hourly = 60,
}

/// <summary>
/// Configuration aggregate: the connected server. The token is NOT stored here —
/// only a reference to it in <see cref="Ports.ISecretStore"/> (invariant).
/// </summary>
/// <param name="Id">Stable account id (<see cref="FolderPair.AccountId"/> refers to it).</param>
/// <param name="BaseUrl">Server address with port and scheme (http in LAN, or https).</param>
/// <param name="UserName">Server login (used for the DAV path).</param>
/// <param name="TokenRef">App-token key in the secret store; never the token itself.</param>
public sealed record Account(Guid Id, string BaseUrl, string UserName, string TokenRef);

/// <summary>
/// "Folder pair" configuration aggregate — the sync object and lock key (invariant).
/// </summary>
/// <param name="Id">Pair identifier (keyed-lock key and baseline-table key).</param>
/// <param name="AccountId">Owning account for the remote side.</param>
/// <param name="RemoteRootPath">Pair root on the server (relative to the user's DAV root).</param>
/// <param name="LocalTreeKey">Local tree key (SAF persisted-URI on Android, root folder in plain-FS);
/// the name a screen shows for the local folder is derived from it, see <see cref="LocalFolderName"/>.</param>
/// <param name="DisplayName">Title of the pair for the UI: stored once at creation from the remote folder
/// name, so it is stable across renames of either side. It is not the name of the local folder.</param>
/// <param name="Schedule">Background run schedule.</param>
/// <param name="Conflict">Conflict strategy for this pair (v1 — KeepBoth only).</param>
/// <param name="Paused">UI pause: the scheduler won't start runs; "Now" still can.</param>
/// <param name="WifiOnly">Network-constraint restriction for background runs (add-pair wizard step 3).</param>
public sealed record FolderPair(
    Guid Id,
    Guid AccountId,
    string RemoteRootPath,
    string LocalTreeKey,
    string DisplayName,
    SyncSchedule Schedule = SyncSchedule.Every15Minutes,
    ConflictStrategy Conflict = ConflictStrategy.KeepBoth,
    bool Paused = false,
    bool WifiOnly = true)
{
    /// <summary>
    /// Name of the local folder for the interface, read out of <paramref name="treeKey"/>: a SAF grant
    /// carries the folder as the document path behind the provider id (<c>…/tree/primary%3ADocuments%2FFErries</c>),
    /// a plain file system root is a directory path, and both end with the folder's own name — so one
    /// last-segment rule covers them, with percent-decoding first because the id separator is an encoded colon.
    /// The result is empty for a key that names nothing, which the caller replaces with its own fallback
    /// rather than showing a raw URI. The provider can answer an exact display name; this derivation is the
    /// offline form that never costs an IPC.
    /// </summary>
    public static string LocalFolderName(string treeKey)
    {
        var decoded = Uri.UnescapeDataString(treeKey ?? string.Empty);
        var separator = decoded.LastIndexOf(':');
        if (separator >= 0)
        {
            decoded = decoded[(separator + 1)..];
        }

        var segments = decoded.Split('/', '\\');
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Trim().Length > 0)
            {
                return segments[i].Trim();
            }
        }

        return string.Empty;
    }
}
