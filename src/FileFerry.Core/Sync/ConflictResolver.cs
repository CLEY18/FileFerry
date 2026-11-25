using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Which side of a resolved conflict the user chose to keep.
/// </summary>
public enum KeepSide
{
    /// <summary>The version living on the main path (the KeepBoth winner) stays; the copies go.</summary>
    Local,

    /// <summary>The version from the conflict copies is promoted onto the main path; everything else goes.</summary>
    Server,
}

/// <summary>
/// The four actions a folder-conflict row offers: rename (one MOVE, zero copies) or delete
/// (every file of the losing subtree passes the stash buffer first) on either side. The kept
/// side then syncs normally — the collision shape is gone after the action.
/// </summary>
public enum FolderAction
{
    /// <summary>MOVE the server entry under a conflict name; the local entry keeps its name.</summary>
    RenameServer,

    /// <summary>MOVE the device entry under a conflict name; the server entry keeps its name.</summary>
    RenameLocal,

    /// <summary>Buffer every server file, then DELETE the server entry.</summary>
    DeleteServer,

    /// <summary>Buffer every device file, then DELETE the device entry.</summary>
    DeleteLocal,
}

/// <summary>
/// The operation plan of one folder-conflict action.
/// </summary>
/// <param name="Main">The conflicting path as the note carries it.</param>
/// <param name="Action">The action the user chose.</param>
/// <param name="Operations">Ordered steps: for a rename one MOVE; for a delete every buffer
/// copy first, then the deletions deepest-first — the executed sequence dissolves the shape.</param>
public sealed record FolderResolvePlan(
    RelativePath Main,
    FolderAction Action,
    IReadOnlyList<SyncOperation> Operations);

/// <summary>
/// The operation plan of one keep-side resolution.
/// </summary>
/// <param name="Main">The conflicted path the user acted on.</param>
/// <param name="Side">The kept side.</param>
/// <param name="Copies">How many conflict-copy records were taken out of the tree.</param>
/// <param name="Operations">Ordered steps: promotion first (when the copy is kept), then every
/// copy's stash-copy and deletions — an executed sequence converges both sides to one version.</param>
public sealed record ResolvePlan(
    RelativePath Main,
    KeepSide Side,
    int Copies,
    IReadOnlyList<SyncOperation> Operations);

/// <summary>
/// Builds a keep-side plan from the current three-way state alone (the determinism invariant —
/// no hidden inputs). Semantics rest on what the KeepBoth run leaves behind: the main path holds
/// the local version on both sides, and each "name (conflicted copy stamp).ext" holds the foreign
/// version on both sides. "Keep local" therefore only discards the copies; "keep server" promotes
/// the newest local copy's content (the one holding the server version) onto the main path and
/// then discards all copies. Every disappearance is preceded by a stash copy —
/// including the overwritten local version on promotion — and the deletions cover each side the
/// record actually lives on.
/// </summary>
public static class ConflictResolver
{
    private const string Marker = " (conflicted copy "; // exactly as the strategy writes the name: <stem><marker><stamp>)<ext>

    /// <summary>
    /// Validates and orders the resolution; rejections: no copies for this path (<see cref="ErrorKind.NotFound"/>),
    /// or "keep local" while the local main path already vanished (nothing to keep).
    /// </summary>
    /// <param name="main">The conflicted file.</param>
    /// <param name="side">The side the user chose.</param>
    /// <param name="local">Fresh local scan, pair coordinates.</param>
    /// <param name="remote">Fresh remote scan, pair coordinates.</param>
    /// <param name="stash">Buffer policy (the copy-before-deletion rule).</param>
    /// <param name="now">Run moment from the clock (stash stamp).</param>
    public static Result<ResolvePlan, SyncError> Build(
        RelativePath main,
        KeepSide side,
        IReadOnlyDictionary<RelativePath, FileSnapshot> local,
        IReadOnlyDictionary<RelativePath, FileSnapshot> remote,
        IStashPolicy stash,
        DateTimeOffset now)
    {
        var copies = FindCopies(main, local, remote);
        if (copies.Count == 0)
        {
            return Result<ResolvePlan, SyncError>.Failure(SyncError.Of(
                ErrorKind.NotFound,
                $"No conflict copies exist for '{main}' — there is nothing to resolve."));
        }

        if (side == KeepSide.Local && !local.ContainsKey(main))
        {
            return Result<ResolvePlan, SyncError>.Failure(SyncError.Of(
                ErrorKind.NotFound,
                $"The local version of '{main}' no longer exists — keeping it is no longer possible."));
        }

        var ops = new List<SyncOperation>();

        if (side == KeepSide.Server)
        {
            // The KeepBoth run mirrored in opposite directions: the LOCAL copy carries the server
            // version, the remote copy carries the local one. So the version to promote can only
            // come from a live local copy — none alive means the server version is already gone.
            var promoted = copies.FindAll(static c => c.Local is not null);
            if (promoted.Count == 0)
            {
                return Result<ResolvePlan, SyncError>.Failure(SyncError.Of(
                    ErrorKind.NotFound,
                    $"No local conflict copy of '{main}' survives — the server version is no longer recoverable."));
            }

            var source = promoted[^1].Local!; // the newest stamp is the newest conflict
            if (local.TryGetValue(main, out var overwritten))
            {
                var keepMain = stash.StashDestination(main, now);
                ops.Add(new SyncOperation(
                    OperationKind.CopyLocal, keepMain, main, overwritten with { Path = keepMain }));
            }

            ops.Add(new SyncOperation(OperationKind.CopyLocal, main, promoted[^1].Path, source with { Path = main }));
            ops.Add(new SyncOperation(
                OperationKind.UploadToRemote, main, null, source with { Path = main, ETag = null }));
        }

        foreach (var (path, copyLocal, copyRemote) in copies)
        {
            if (copyLocal is { } live)
            {
                var stashPath = stash.StashDestination(path, now);
                ops.Add(new SyncOperation(OperationKind.CopyLocal, stashPath, path, live with { Path = stashPath }));
                ops.Add(new SyncOperation(OperationKind.DeleteLocal, path, null, live));
            }

            if (copyRemote is { } online && copyLocal is null)
            {
                // vanished locally — the buffer is filled from the server before the remote delete
                var stashPath = stash.StashDestination(path, now);
                ops.Add(new SyncOperation(OperationKind.DownloadToLocal, stashPath, path, online with { Path = stashPath }));
            }

            if (copyRemote is not null)
            {
                ops.Add(new SyncOperation(OperationKind.DeleteRemote, path, null, copyRemote));
            }
        }

        return Result<ResolvePlan, SyncError>.Success(new ResolvePlan(main, side, copies.Count, ops));
    }

    /// <summary>
    /// Builds one folder-conflict action from the current three-way state alone. The chosen
    /// side is located by exact path first, then by case twin (the note carries one side's
    /// spelling; a collection has no extension to copy around, so a rename appends the conflict
    /// suffix to the folder's whole name). A delete stashes every file of the subtree before
    /// any disappearance and empties the folders deepest-first.
    /// </summary>
    /// <param name="main">The conflicting path, as the note carries it.</param>
    /// <param name="action">Rename or delete, on the server or on the device.</param>
    /// <param name="local">Fresh local scan, pair coordinates.</param>
    /// <param name="remote">Fresh remote scan, pair coordinates.</param>
    /// <param name="stash">Buffer policy (the copy-before-deletion rule).</param>
    /// <param name="now">Run moment from the clock (conflict name + stash stamp).</param>
    public static Result<FolderResolvePlan, SyncError> BuildFolder(
        RelativePath main,
        FolderAction action,
        IReadOnlyDictionary<RelativePath, FileSnapshot> local,
        IReadOnlyDictionary<RelativePath, FileSnapshot> remote,
        IStashPolicy stash,
        DateTimeOffset now)
    {
        var onServer = action is FolderAction.RenameServer or FolderAction.DeleteServer;
        var side = onServer ? remote : local;
        var entryPath = Locate(side, main);
        if (entryPath is null)
        {
            return Result<FolderResolvePlan, SyncError>.Failure(SyncError.Of(
                ErrorKind.NotFound,
                $"The {(onServer ? "server" : "local")} side of '{main}' no longer exists — there is nothing to {(action is FolderAction.RenameServer or FolderAction.RenameLocal ? "rename" : "delete")}."));
        }

        var entry = side[entryPath];
        var ops = new List<SyncOperation>();

        if (action is FolderAction.RenameServer or FolderAction.RenameLocal)
        {
            var target = ConflictFolderName(entryPath, entry, now);
            if (side.ContainsKey(target))
            {
                return Result<FolderResolvePlan, SyncError>.Failure(SyncError.Of(
                    ErrorKind.Conflict, $"The conflict name '{target}' is already taken."));
            }

            ops.Add(new SyncOperation(
                onServer ? OperationKind.MoveRemote : OperationKind.MoveLocal,
                target, entryPath, entry with { Path = target }));
            return Result<FolderResolvePlan, SyncError>.Success(new FolderResolvePlan(main, action, ops));
        }

        // Delete: every file of the losing subtree gets its buffer copy first (stop rule),
        // then the deletions run deepest-first — the folders leave emptied, never with children.
        if (!entry.IsDirectory)
        {
            var own = stash.StashDestination(entryPath, now);
            ops.Add(onServer
                ? new SyncOperation(OperationKind.DownloadToLocal, own, entryPath, entry with { Path = own })
                : new SyncOperation(OperationKind.CopyLocal, own, entryPath, entry with { Path = own }));
        }

        var subtree = side
            .Where(kv => !Equals(kv.Key, entryPath) && kv.Key.StartsWith(entryPath))
            .OrderBy(static kv => kv.Key.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (var (path, snapshot) in subtree.Where(static kv => !kv.Value.IsDirectory))
        {
            var buffer = stash.StashDestination(path, now);
            ops.Add(onServer
                ? new SyncOperation(OperationKind.DownloadToLocal, buffer, path, snapshot with { Path = buffer })
                : new SyncOperation(OperationKind.CopyLocal, buffer, path, snapshot with { Path = buffer }));
        }

        foreach (var (path, snapshot) in subtree
                     .OrderByDescending(static kv => kv.Key.Depth)
                     .ThenBy(static kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            ops.Add(new SyncOperation(
                onServer ? OperationKind.DeleteRemote : OperationKind.DeleteLocal, path, null, snapshot));
        }

        ops.Add(new SyncOperation(
            onServer ? OperationKind.DeleteRemote : OperationKind.DeleteLocal, entryPath, null, entry));
        return Result<FolderResolvePlan, SyncError>.Success(new FolderResolvePlan(main, action, ops));
    }

    /// <summary>
    /// The side's entry for the conflicting path: the note's spelling exactly, else its unique
    /// case twin in the same parent (a case collision stores the two spellings on different sides).
    /// </summary>
    private static RelativePath? Locate(IReadOnlyDictionary<RelativePath, FileSnapshot> side, RelativePath main)
    {
        if (side.ContainsKey(main))
        {
            return main;
        }

        return side.Keys
            .Where(key => Equals(key.Parent, main.Parent) && key.EqualsIgnoreCase(main))
            .OrderBy(static key => key.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The conflict rename target: a collection takes the suffix on its whole name (a folder
    /// has no content-bearing extension to keep outside), a file around its extension.
    /// </summary>
    private static RelativePath ConflictFolderName(RelativePath path, FileSnapshot entry, DateTimeOffset now)
    {
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = $" (conflict {stamp})";
        var name = entry.IsDirectory
            ? path.Name + suffix
            : System.IO.Path.GetFileNameWithoutExtension(path.Name) + suffix + System.IO.Path.GetExtension(path.Name);
        return path.Parent.Combine(name);
    }

    /// <summary>
    /// Sibling copies of the main path, oldest stamp first — name-shaped exactly like the
    /// conflict-copy naming, so a user file merely containing the marker stays untouched.
    /// </summary>
    private static List<(RelativePath Path, FileSnapshot? Local, FileSnapshot? Remote)> FindCopies(
        RelativePath main,
        IReadOnlyDictionary<RelativePath, FileSnapshot> local,
        IReadOnlyDictionary<RelativePath, FileSnapshot> remote)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(main.Name);
        var extension = System.IO.Path.GetExtension(main.Name);
        var parent = main.Parent;

        return local.Keys.Concat(remote.Keys)
            .Where(key => key != main
                && key.Parent == parent
                && IsCopyOf(key.Name, stem, extension))
            .Distinct()
            .OrderBy(static name => name.ToString(), StringComparer.Ordinal) // the UTC stamp in the name sorts by time
            .Select(path => (path, local.GetValueOrDefault(path), remote.GetValueOrDefault(path)))
            .ToList();
    }

    private static bool IsCopyOf(string name, string stem, string extension)
    {
        if (!name.StartsWith(stem, StringComparison.Ordinal)
            || !name.EndsWith(extension, StringComparison.Ordinal)
            || name.Length != stem.Length + Marker.Length + 15 + 1 + extension.Length
            || !name.AsSpan(stem.Length, Marker.Length).Equals(Marker, StringComparison.Ordinal))
        {
            return false; // "<stem> (conflicted copy yyyymmdd'T'HHmmss)<ext>" — the stamp is 14 digits around a 'T'
        }

        var stamp = name[(stem.Length + Marker.Length)..(name.Length - extension.Length - 1)];
        return stamp.Length == 15
            && stamp[8] == 'T'
            && stamp[..8].All(char.IsAsciiDigit)
            && stamp[9..].All(char.IsAsciiDigit);
    }
}
