using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Sync;

/// <summary>
/// Evidence hatch: before DiffEngine, backfills <see cref="FileSnapshot.ContentHash"/> wherever the
/// "same content?" decision hangs on bare size: a file on both sides with equal size but no strong
/// evidence — or a legacy baseline row without ETag/hash. Only a narrow candidate set is hashed
/// (both sides, same size), never the whole tree. Evidence already proven earlier is reused 
/// the baseline — repeat runs don't re-hash. Determinism is intact: this is IO through
/// ports, with no hidden state.
/// </summary>
internal static class EvidenceHatch
{
    /// <summary>
    /// Add hash evidence to the side snapshots; returns updated lists (inputs are not mutated).
    /// A read/hash failure on a candidate leaves it without evidence (DiffEngine then yields a
    /// conservative KeepBoth conflict — safer than a silent merge).
    /// </summary>
    /// <param name="local">Local side — reads candidate files for hashing.</param>
    /// <param name="remote">Remote side — reads the remote halves of candidates.</param>
    /// <param name="hasher">Hash provider (the same algorithm as in the baseline cache).</param>
    /// <param name="localScan">Local-scan snapshot of the run — one half of the candidate set.</param>
    /// <param name="remoteScan">Remote-scan snapshot — the other half of the candidate set.</param>
    /// <param name="baseline">The baseline (evidence cache).</param>
    /// <param name="root">The pair's remote root — coordinates for remote port calls.</param>
    /// <param name="ct">Cancels the in-flight read/hash IO.</param>
    public static async Task<(IReadOnlyList<FileSnapshot> Local, IReadOnlyList<FileSnapshot> Remote)> HatchAsync(
        ILocalFileSystem local,
        IWritableRemoteStore remote,
        IHashProvider hasher,
        IReadOnlyList<FileSnapshot> localScan,
        IReadOnlyList<FileSnapshot> remoteScan,
        IReadOnlyList<FileSnapshot> baseline,
        RemotePath root,
        CancellationToken ct)
    {
        var remoteIdx = remoteScan.ToDictionary(static s => s.Path, static s => s);
        var baselineIdx = baseline.ToDictionary(static s => s.Path, static s => s);
        var localPatch = new Dictionary<RelativePath, FileSnapshot>();
        var remotePatch = new Dictionary<RelativePath, FileSnapshot>();

        foreach (var l in localScan)
        {
            if (l.IsDirectory
                || !remoteIdx.TryGetValue(l.Path, out var r) || r.IsDirectory
                || l.Size != r.Size)
            {
                continue; // different sizes need no evidence — the diff is unambiguous anyway
            }

            baselineIdx.TryGetValue(l.Path, out var b);
            var silentMergeAtStake = b is null && !l.SameByStrongEvidence(r); // would "merge silently" on bare size
            var legacyRowAtStake = b is not null && b.ETag is null && b.ContentHash is null; // legacy row

            if (!silentMergeAtStake && !legacyRowAtStake)
            {
                continue;
            }

            var localHash = ReusableHash(b, l) ?? await HashLocalAsync(local, hasher, l.Path, ct).ConfigureAwait(false);
            var remoteHash = ReusableHash(b, r)
                ?? r.ContentHash
                ?? await HashRemoteAsync(remote, hasher, root.Combine(l.Path), ct).ConfigureAwait(false);

            if (localHash is null || remoteHash is null)
            {
                continue; // don't buffer half of the evidence — let the planner give a conservative conflict
            }

            localPatch[l.Path] = l with { ContentHash = localHash };
            remotePatch[r.Path] = r with { ContentHash = remoteHash };
        }

        return (Apply(localScan, localPatch), Apply(remoteScan, remotePatch));
    }

    /// <summary>
    /// Cache from the baseline: a row with a hash whose content weakly matches the side — same evidence.
    /// </summary>
    private static string? ReusableHash(FileSnapshot? baselineSide, FileSnapshot side) =>
        baselineSide is { ContentHash: { } hash } && baselineSide.SameContentAs(side) ? hash : null;

    private static async Task<string?> HashLocalAsync(
        ILocalFileSystem local, IHashProvider hasher, RelativePath path, CancellationToken ct)
    {
        var read = await local.ReadAsync(path, ct).ConfigureAwait(false);
        return read.IsFailure ? null : await HashAsync(read.Value, hasher, ct).ConfigureAwait(false);
    }

    private static async Task<string?> HashRemoteAsync(
        IWritableRemoteStore remote, IHashProvider hasher, RemotePath path, CancellationToken ct)
    {
        var read = await remote.ReadAsync(path, ct).ConfigureAwait(false);
        return read.IsFailure ? null : await HashAsync(read.Value, hasher, ct).ConfigureAwait(false);
    }

    private static async Task<string?> HashAsync(Stream stream, IHashProvider hasher, CancellationToken ct)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await hasher.HashAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null; // rogue candidate — no evidence, not a run failure
            }
        }
    }

    private static IReadOnlyList<FileSnapshot> Apply(
        IReadOnlyList<FileSnapshot> scan, Dictionary<RelativePath, FileSnapshot> patch) =>
        patch.Count == 0 ? scan : scan.Select(s => patch.TryGetValue(s.Path, out var p) ? p : s).ToArray();
}
