using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync;

/// <summary>
/// ScanRemote: recursive walk of the remote tree with <c>PROPFIND Depth:1</c> through
/// the <see cref="IRemoteBrowser"/> port. Missing root → empty tree (first run into a new folder);
/// a subtree missing mid-walk → a partial scan failure that stops the run.
/// ETag pruning: a collection's ETag matches the baseline → the subtree isn't scanned and the
/// baseline subtree stands in for it (the server guarantees: any descendant re-linking changes
/// the ancestor's ETag).
/// </summary>
public static class RemoteScanner
{
    /// <summary>
    /// Full (pruning-aware) snapshot of the pair's remote root in <see cref="RelativePath"/> coordinates.
    /// </summary>
    /// <param name="remote">Remote side (reads).</param>
    /// <param name="root">The pair's root relative to the user's DAV-root.</param>
    /// <param name="baseline">The pair's baseline — source of collection ETags for pruning.</param>
    /// <param name="ct">Run cancellation (checked between folder listings).</param>
    public static async Task<Result<IReadOnlyList<FileSnapshot>, SyncError>> ScanAsync(
        IRemoteBrowser remote, RemotePath root, IReadOnlyList<FileSnapshot> baseline, CancellationToken ct)
    {
        var baselineIndex = baseline.DistinctBy(static s => s.Path).ToDictionary(static s => s.Path, static s => s);
        var found = new List<FileSnapshot>();
        var queue = new Queue<RemotePath>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = queue.Dequeue();
            var listing = await remote.ListAsync(folder, ct).ConfigureAwait(false);

            if (listing.IsFailure)
            {
                // A missing root isn't a failure — it's "the server isn't populated yet" for this pair.
                if (folder.Equals(root) && listing.Error.Kind == ErrorKind.NotFound)
                {
                    break;
                }

                return Result<IReadOnlyList<FileSnapshot>, SyncError>.Failure(listing.Error);
            }

            var children = listing.Value.ToList();
            children.Sort(static (x, y) => x.Path.CompareTo(y.Path)); // deterministic level traversal order
            foreach (var entry in children)
            {
                if (entry.Path.Equals(folder))
                {
                    continue; // a Depth:1 response includes the collection itself — not a child
                }

                found.Add(entry.ToSnapshot(root));
                if (entry.Kind != EntryKind.Directory)
                {
                    continue;
                }

                var relative = entry.Path.RelativeTo(root);
                if (CanPrune(entry, relative, baselineIndex))
                {
                    // The directory itself is already added as a fresh entry — from the baseline we keep only descendants.
                    found.AddRange(baselineIndex.Values.Where(
                        s => !s.Path.Equals(relative) && s.Path.StartsWith(relative)));
                }
                else
                {
                    queue.Enqueue(entry.Path);
                }
            }
        }

        found.Sort(static (a, b) => a.Path.CompareTo(b.Path));
        return Result<IReadOnlyList<FileSnapshot>, SyncError>.Success(found);
    }

    /// <summary>
    /// Pruning is possible only when both collection ETags are known and equal (without evidence — scan).
    /// </summary>
    private static bool CanPrune(
        RemoteEntry entry, RelativePath relative, IReadOnlyDictionary<RelativePath, FileSnapshot> baseline) =>
        entry.ETag is { } etag
        && baseline.TryGetValue(relative, out var known)
        && known.ETag == etag;
}
