using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Snapshot index keyed by canonical path, tolerant by construction: a listing may carry one
/// name in two Unicode spellings, which fold onto the same path, and a strict dictionary would
/// throw there. The fold keeps one deterministic winner per name (newest, ties by metadata,
/// never by listing order) and hands the colliding names to the journal note — the sync treats
/// the name as the one entry the winner is.
/// </summary>
internal static class SnapshotIndex
{
    /// <summary>
    /// Index a scan; names that appear more than once after folding are added to
    /// <paramref name="collisions"/> (each of them once).
    /// </summary>
    /// <param name="snapshots">Scan result in pair coordinates.</param>
    /// <param name="collisions">Optional collector of the folded-duplicate names.</param>
    internal static IReadOnlyDictionary<RelativePath, FileSnapshot> From(
        IReadOnlyList<FileSnapshot> snapshots, ICollection<RelativePath>? collisions = null)
    {
        var index = new Dictionary<RelativePath, FileSnapshot>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (!index.TryGetValue(snapshot.Path, out var kept))
            {
                index[snapshot.Path] = snapshot;
                continue;
            }

            collisions?.Add(snapshot.Path);
            if (Wins(snapshot, kept))
            {
                index[snapshot.Path] = snapshot;
            }
        }

        return index;
    }

    /// <summary>
    /// Deterministic total order over the metadata visible to the model: newer time wins;
    /// then the smaller ETag, the smaller hash, the smaller size; a fully indistinguishable
    /// spelling keeps the slot of the one already kept.
    /// </summary>
    private static bool Wins(FileSnapshot candidate, FileSnapshot kept)
    {
        var byTime = DateTimeOffset.Compare(
            candidate.ModifiedAt ?? DateTimeOffset.MinValue, kept.ModifiedAt ?? DateTimeOffset.MinValue);
        if (byTime != 0)
        {
            return byTime > 0;
        }

        var byETag = string.CompareOrdinal(candidate.ETag ?? string.Empty, kept.ETag ?? string.Empty);
        if (byETag != 0)
        {
            return byETag < 0;
        }

        var byHash = string.CompareOrdinal(candidate.ContentHash ?? string.Empty, kept.ContentHash ?? string.Empty);
        if (byHash != 0)
        {
            return byHash < 0;
        }

        return candidate.Size != kept.Size && candidate.Size < kept.Size;
    }
}
