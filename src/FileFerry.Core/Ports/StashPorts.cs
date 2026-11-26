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
public sealed record StashEntry(
    Guid PairId,
    RelativePath StashPath,
    RelativePath OriginPath,
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
}
