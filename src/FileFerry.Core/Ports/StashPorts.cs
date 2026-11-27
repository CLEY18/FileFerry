using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Ports;

/// <summary>
/// Stash-buffer record ("Stash buffer", table <c>stash_meta</c>): a copy of a deleted
/// entry with a creation moment for TTL. The physical copy lives in <c>FileFerry Trash/</c> inside
/// the pair's local root; this record is its index for the trash-buffer UI and for TTL-purge.
/// </summary>
/// <param name="PairId">Pair owning the copy.</param>
/// <param name="StashPath">Copy path relative to the local root (inside <c>FileFerry Trash/</c>).</param>
/// <param name="OriginPath">Removed entry (relative to the local root) — where the copy came.</param>
/// <param name="CreatedAt">Copy creation moment (base of TTL arithmetic, from the run's <see cref="IClock"/>).</param>
/// <param name="TreeKey">Local tree the copy lives in; the storage fills it from the pair's grant
/// at insert time — callers pass nothing and a retargeted pair keeps its old copies' address.</param>
public sealed record StashEntry(
    Guid PairId,
    RelativePath StashPath,
    RelativePath OriginPath,
    DateTimeOffset CreatedAt,
    string TreeKey = "");

/// <summary>
/// A copy whose pair no longer exists: the buffer file is still sitting in someone's tree,
/// and the row now carries the tree key — the address the orphan sweep needs after the pair
/// row itself is gone. The copy stays retrievable until the TTL ends, exactly as promised
/// while the pair was alive; the sweep only collects it once expired.
/// </summary>
/// <param name="PairId">The pair that made the copy (index PK part, kept as provenance).</param>
/// <param name="TreeKey">Local tree the copy lives in (the storage joins it in at insert time).</param>
/// <param name="StashPath">Copy path relative to that tree's root.</param>
/// <param name="CreatedAt">Copy moment — the same TTL basis as a live pair's copy.</param>
public sealed record OrphanedStashEntry(
    Guid PairId,
    string TreeKey,
    RelativePath StashPath,
    DateTimeOffset CreatedAt);

/// <summary>
/// Persistence of the stash-buffer index (SQLite in Data). Writes happen in the executor inside
/// the delete's UoW (invariant: copy first, then delete); reads — pair details / trash buffer screen;
/// index removal — the TTL purge (a restore keeps its copy until retention ends).
/// </summary>
public interface IStashRepository
{
    /// <summary>
    /// Add/overwrite a copy index (PK — pair + copy path).
    /// </summary>
    Task AddAsync(StashEntry entry, CancellationToken ct);

    /// <summary>
    /// All indexes of a pair, hottest first (fresh copies on top of the trash-buffer list).
    /// </summary>
    Task<IReadOnlyList<StashEntry>> ListAsync(Guid pairId, CancellationToken ct);

    /// <summary>
    /// Remove a copy index (after a TTL purge).
    /// </summary>
    /// <param name="pairId">Pair owning the copy (index PK part).</param>
    /// <param name="stashPath">Copy path within the local root.</param>
    /// <param name="ct">Cancels the index write.</param>
    Task RemoveAsync(Guid pairId, RelativePath stashPath, CancellationToken ct);

    /// <summary>
    /// Indexes whose pair no longer exists, hottest first — the reading of the orphan sweep.
    /// Deleting a pair (or the whole account) removes configuration, never buffer copies:
    /// their rows outlive it, each carrying the tree the copy sits in. The <c>TreeKey</c>
    /// is filled by the storage from the pair's grant at insert time, not by the caller.
    /// </summary>
    /// <param name="ct">Cancels the read.</param>
    Task<IReadOnlyList<OrphanedStashEntry>> ListOrphansAsync(CancellationToken ct);
}
