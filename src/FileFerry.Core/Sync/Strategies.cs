using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Default conflict strategy: no version is lost — both end up on both sides.
/// The local version stays canonical on the "primary" path, the two extras live under
/// "name (conflicted copy…).ext". Deterministic: the copy name depends only on <see cref="Change"/> + now.
/// </summary>
public sealed class KeepBothStrategy : IConflictResolutionStrategy
{
    /// <inheritdoc cref="IConflictResolutionStrategy.ConflictCopyName"/>
    public RelativePath ConflictCopyName(RelativePath path, DateTimeOffset now)
    {
        var name = path.Name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        // the UTC stamp is stable for any device time zone; the stem fallback prevents "."-name collapse
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var copy = $"{(string.IsNullOrEmpty(stem) ? name : stem)} (conflicted copy {stamp}){extension}";
        return path.Parent.Combine(copy);
    }

    /// <inheritdoc cref="IConflictResolutionStrategy.Resolve"/>
    public IReadOnlyList<SyncOperation> Resolve(Change change, DateTimeOffset now)
    {
        var l = change.Local;
        var r = change.Remote;

        // delete-vs-modify: only one side is alive — it becomes canonical on both sides
        if (l is null && r is not null)
        {
            return [new SyncOperation(OperationKind.DownloadToLocal, change.Path, null, r)];
        }

        if (r is null && l is not null)
        {
            return [new SyncOperation(OperationKind.UploadToRemote, change.Path, null, l)];
        }

        var copyName = ConflictCopyName(change.Path, now);
        return
        [
            // 1) the remote version stays locally under the conflict name (source — the primary path on the server)
            new SyncOperation(OperationKind.DownloadToLocal, copyName, change.Path, r! with { Path = copyName }),
            // 2) the local version is duplicated on the server under the conflict name (source — the primary path locally)
            new SyncOperation(OperationKind.UploadToRemote, copyName, change.Path, l! with { Path = copyName }),
            // 3) the primary path converges to the local version on both sides
            new SyncOperation(OperationKind.UploadToRemote, change.Path, null, l),
        ];
    }
}

/// <summary>
/// Stash-buffer policy: a copy before any deletion, a timestamped directory
/// (uniqueness + the basis of the TTL-purge), the inner path preserves the pair structure.
/// </summary>
/// <param name="options">Settings (folder, TTL).</param>
public sealed class StashPolicy(StashPolicyOptions options) : IStashPolicy
{
    /// <summary>
    /// Policy settings.
    /// </summary>
    public StashPolicyOptions Options { get; } = options;

    /// <inheritdoc cref="IStashPolicy.StashRoot"/>
    public RelativePath StashRoot { get; } = RelativePath.Parse(options.FolderName);

    /// <inheritdoc cref="IStashPolicy.StashDestination"/>
    public RelativePath StashDestination(RelativePath path, DateTimeOffset now)
    {
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return RelativePath.Parse($"{Options.FolderName}/{stamp}").Combine(path);
    }

    /// <summary>
    /// Whether the copy's TTL has passed by its timestamp (the purge stage at the end of the run —
    /// <see cref="StashPurger"/>).
    /// </summary>
    /// <param name="createdAt">Copy creation time (<c>stash_meta</c> index row).</param>
    /// <param name="now">Current moment per <c>IClock</c>.</param>
    public bool IsExpired(DateTimeOffset createdAt, DateTimeOffset now) =>
        now - createdAt > Options.Ttl;
}

/// <summary>
/// Stash settings (Options pattern; DI overrides from feature-flag configuration).
/// </summary>
public sealed record StashPolicyOptions
{
    /// <summary>
    /// Buffer folder name relative to the pair's local root. No dot: dot-documents in SAF are
    /// provider-defined behavior (MediaProvider treats them differently), and the buffer must be
    /// reliable on every provider. (The original folder-creation blocker wasn't the dot at all —
    /// a wrong MIME_TYPE_DIR; the name stays dotless anyway.)
    /// </summary>
    public string FolderName { get; init; } = "FileFerry Trash";

    /// <summary>
    /// Recovery window: after the TTL copies are purged (default — 30 days, matching the pair details UI dialogs).
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromDays(30);
}
