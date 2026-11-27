namespace FileFerry.App.Services;

/// <summary>
/// Collects buffer copies whose pair no longer exists: deleting a pair (or signing out) keeps
/// the copies retrievable for the rest of their TTL — exactly what the delete dialog promises —
/// and once the window ends, this pass is who carries them out. The run-time purge only sees
/// live pairs, so the sweep runs at startup, before any screen can list a buffer.
/// </summary>
public interface IOrphanedCopySweeper
{
    /// <summary>
    /// One sweep: expired orphans are deleted and their index rows removed; a tree whose
    /// access is gone keeps everything intact for a later boot. Never throws — the app
    /// starts either way, an unfinished collection is not a reason to stop it.
    /// </summary>
    /// <param name="ct">Cancels the pass; the rest waits for the next start.</param>
    Task SweepAsync(CancellationToken ct);
}
