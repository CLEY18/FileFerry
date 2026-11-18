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
