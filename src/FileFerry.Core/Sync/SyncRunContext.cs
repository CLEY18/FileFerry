using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Sync;

/// <summary>
/// Explicit input of a sync run ("explicit state instead of ambient static"):
/// three snapshot sets + context. Determinism on (local, remote, baseline, now) — the determinism invariant.
/// </summary>
/// <param name="Pair">The folder pair the run was started for.</param>
/// <param name="Now">The run moment from <see cref="Ports.IClock"/> (for copy/stash names, no hidden Now).</param>
/// <param name="Local">Local-side snapshot, keyed by relative path.</param>
/// <param name="Remote">Remote-side snapshot (already in pair coordinates via <c>RemotePath.RelativeTo</c>).</param>
/// <param name="Baseline">Last agreed state, keyed by relative path — the third Diff input.</param>
public sealed record SyncRunContext(
    FolderPair Pair,
    DateTimeOffset Now,
    IReadOnlyDictionary<RelativePath, FileSnapshot> Local,
    IReadOnlyDictionary<RelativePath, FileSnapshot> Remote,
    IReadOnlyDictionary<RelativePath, FileSnapshot> Baseline);

/// <summary>
/// Conflict-resolution strategy (Strategy). Takes a conflicting change and emits
/// operations that preserve both sides; does not weaken the stash invariant (L).
/// </summary>
public interface IConflictResolutionStrategy
{
    /// <summary>
    /// Canonical conflict-copy name for <paramref name="path"/> ("name (conflicted copy).ext").
    /// </summary>
    /// <param name="path">The conflicting record's path (pair coordinates).</param>
    /// <param name="now">The run moment (part of the name for uniqueness).</param>
    RelativePath ConflictCopyName(RelativePath path, DateTimeOffset now);

    /// <summary>
    /// Operations that keep both versions on both sides for a single conflict.
    /// </summary>
    /// <param name="change">The conflict (<see cref="ChangeKind.Both"/>).</param>
    /// <param name="now">The run moment — feeds the conflict-copy names' uniqueness.</param>
    IReadOnlyList<SyncOperation> Resolve(Change change, DateTimeOffset now);
}

/// <summary>
/// Stash-buffer policy: where to copy a record before deletion (loss safeguard, invariant).
/// </summary>
public interface IStashPolicy
{
    /// <summary>
    /// Root of the stash folder relative to the pair's local root (the executor's guard).
    /// </summary>
    RelativePath StashRoot { get; }

    /// <summary>
    /// Path under <c>FileFerry Trash/</c> where a copy of <paramref name="path"/> goes before deletion.
    /// </summary>
    /// <param name="path">The record about to be deleted (pair coordinates).</param>
    /// <param name="now">The run moment (part of the path for uniqueness and TTL computation).</param>
    RelativePath StashDestination(RelativePath path, DateTimeOffset now);

    /// <summary>
    /// Whether a copy created at <paramref name="createdAt"/> has passed its TTL as of <paramref name="now"/>.
    /// </summary>
    /// <param name="createdAt">Copy creation time (from <c>stash_meta</c>).</param>
    /// <param name="now">The moment of the check, from <see cref="Ports.IClock"/>.</param>
    bool IsExpired(DateTimeOffset createdAt, DateTimeOffset now);
}
