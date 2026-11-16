using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync;

/// <summary>
/// BaselineUpdater stage: the new baseline comes <b>only from successfully applied</b>
/// operations (invariant, one UoW). Projection is a pure function; the swap is a single
/// SQLite transaction via <see cref="ISyncStateRepository.ReplaceBaselineAsync"/>.
/// </summary>
/// <param name="state">The pair's state repository.</param>
/// <param name="stash">Stash policy: copies in <c>FileFerry Trash/</c> are not sync state.</param>
public sealed class BaselineUpdater(ISyncStateRepository state, IStashPolicy stash)
{
    /// <summary>
    /// "baseline + applied operations → next baseline" projection (pure function).
    /// </summary>
    /// <param name="baseline">Current baseline — the projection's starting point; never mutated.</param>
    /// <param name="applied">Operations in Applied state, in execution order.</param>
    /// <param name="stashRoot">Stash root — every path under it is ignored.</param>
    /// <returns>The new baseline (a new dictionary; the input is not mutated).</returns>
    public static IReadOnlyDictionary<RelativePath, FileSnapshot> Project(
        IReadOnlyDictionary<RelativePath, FileSnapshot> baseline,
        IReadOnlyList<SyncOperation> applied,
        RelativePath stashRoot)
    {
        var next = new Dictionary<RelativePath, FileSnapshot>(baseline);

        foreach (var op in applied)
        {
            if (op.Path.StartsWith(stashRoot) || (op.SourcePath?.StartsWith(stashRoot) ?? false))
            {
                continue; // the stash buffer is outside sync state
            }

            switch (op.Kind)
            {
                case OperationKind.DeleteLocal or OperationKind.DeleteRemote:
                    next.Remove(op.Path);
                    break;

                case OperationKind.MoveLocal or OperationKind.MoveRemote when op.SourcePath is { } source:
                    next.Remove(source);
                    next[op.Path] = op.After ?? next.GetValueOrDefault(source) ?? baseline[source];
                    break;

                default:
                    if (op.After is { } after)
                    {
                        next[op.Path] = after;
                    }

                    break;
            }
        }

        return next;
    }

    /// <summary>
    /// Compute and write the new baseline (UoW boundary: one replacement = one transaction).
    /// </summary>
    /// <param name="pairId">The pair whose baseline row set is replaced.</param>
    /// <param name="applied">The run's applied operations (from <c>ExecutionReport.Applied</c>).</param>
    /// <param name="remoteScanned">The run's remote-scan snapshot: its ETags carry into the baseline —
    /// they enable ETag pruning on the next scan and heal legacy rows without evidence;
    /// <see langword="null"/> — no backfill. The scan never seeds new rows.</param>
    /// <param name="localScanned">The local-scan snapshot (with hatch hashes): caches the evidence
    /// the baseline so later runs don't re-hash; <see langword="null"/> — no backfill.</param>
    /// <param name="ct">Cancellation; the replacement UoW is never half-applied.</param>
    public async Task ApplyAsync(
        Guid pairId, IReadOnlyList<SyncOperation> applied,
        IReadOnlyList<FileSnapshot>? remoteScanned = null,
        IReadOnlyList<FileSnapshot>? localScanned = null,
        CancellationToken ct = default)
    {
        var current = await state.LoadBaselineAsync(pairId, ct).ConfigureAwait(false);
        var indexed = current.ToDictionary(static s => s.Path, static s => s);
        var projected = new Dictionary<RelativePath, FileSnapshot>(
            Project(indexed, applied, stash.StashRoot)); // copy: fresh scan evidence is merged in here

        foreach (var scanned in (remoteScanned ?? []).Where(static s => s.ETag is not null))
        {
            // Directories: the scan's ETag cache (pruning) — fresher is always right; files: backfill without evidence only.
            if (scanned.IsDirectory && projected.TryGetValue(scanned.Path, out var dir))
            {
                projected[scanned.Path] = dir with { ETag = scanned.ETag };
            }
            else
            {
                BackfillEvidence(projected, scanned, etag: scanned.ETag, hash: null);
            }
        }

        foreach (var scanned in (localScanned ?? []).Where(static s => s.ContentHash is not null))
        {
            BackfillEvidence(projected, scanned, etag: null, hash: scanned.ContentHash);
        }

        var ordered = new List<FileSnapshot>(projected.Values);
        ordered.Sort(static (a, b) => a.Path.CompareTo(b.Path));
        await state.ReplaceBaselineAsync(pairId, ordered, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Evidence backfill: a baseline row without ETag/hash receives evidence from a scan only when
    /// the size matches (same content per the available evidence) and it has no evidence of its own.
    /// This is not a state move (is about applied operations) but recording evidence about an
    /// already-shared state; the scan never adds new rows.
    /// </summary>
    private static void BackfillEvidence(
        Dictionary<RelativePath, FileSnapshot> projected, FileSnapshot scanned, string? etag, string? hash)
    {
        if (!projected.TryGetValue(scanned.Path, out var row) || row.Size != scanned.Size)
        {
            return;
        }

        if (etag is not null && row.ETag is null)
        {
            row = row with { ETag = etag };
        }

        if (hash is not null && row.ContentHash is null)
        {
            row = row with { ContentHash = hash };
        }

        projected[scanned.Path] = row;
    }
}
