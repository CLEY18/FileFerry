using FileFerry.Core.Domain;

namespace FileFerry.Core.Ports;

/// <summary>
/// Persistence of sync state (SQLite in Data): pair baseline, operation write-ahead journal, run journal.
/// All methods run under one outer Unit of Work: invariant "baseline only for applied ops".
/// </summary>
public interface ISyncStateRepository
{
    /// <summary>
    /// Load the pair's baseline (empty list if no runs happened yet).
    /// </summary>
    Task<IReadOnlyList<FileSnapshot>> LoadBaselineAsync(Guid pairId, CancellationToken ct);

    /// <summary>
    /// Fully replace the pair's baseline (called only inside a UoW after successful operations).
    /// </summary>
    /// <param name="pairId">Baseline table key — every row of this pair is replaced.</param>
    /// <param name="baseline">New full snapshot of the last shared state.</param>
    /// <param name="ct">Cancels the write inside the enclosing UoW.</param>
    Task ReplaceBaselineAsync(Guid pairId, IReadOnlyList<FileSnapshot> baseline, CancellationToken ct);

    /// <summary>
    /// Append a write-ahead-journal record (Planned — before execution, Applied/Failed — after).
    /// </summary>
    /// <param name="pairId">Pair whose journal receives the row.</param>
    /// <param name="entry">Journal record (Seq is assigned monotonically by the repository).</param>
    /// <param name="ct">Cancels the append inside the enclosing UoW.</param>
    Task AppendJournalAsync(Guid pairId, JournalEntry entry, CancellationToken ct);

    /// <summary>
    /// All live write-ahead-journal records of the pair in Seq order (resume contract): the executor
    /// skips <see cref="JournalState.Applied"/> and repeats Planned/Failed — handlers are idempotent.
    /// </summary>
    Task<IReadOnlyList<JournalEntry>> LoadJournalAsync(Guid pairId, CancellationToken ct);

    /// <summary>
    /// Fold executed/unneeded records after a run completes successfully.
    /// </summary>
    Task ClearJournalAsync(Guid pairId, CancellationToken ct);

    /// <summary>
    /// Write/update the run's summary with events (UoW boundary).
    /// </summary>
    Task SaveRunLogAsync(SyncRunLog runLog, CancellationToken ct);

    /// <summary>
    /// The pair's latest <paramref name="limit"/> run logs (the journal screen).
    /// </summary>
    Task<IReadOnlyList<SyncRunLog>> RecentRunsAsync(Guid pairId, int limit, CancellationToken ct);
}

/// <summary>
/// CRUD of configuration aggregates (accounts/folder_pairs tables in Data).
/// </summary>
/// <typeparam name="TEntity">Aggregate (<see cref="Account"/> or <see cref="FolderPair"/>).</typeparam>
public interface IRepository<TEntity>
{
    /// <summary>
    /// All aggregates (in creation order).
    /// </summary>
    Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Find by identifier; <see langword="null"/> if absent.
    /// </summary>
    Task<TEntity?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Insert or update (save-or-update by Id).
    /// </summary>
    Task SaveAsync(TEntity entity, CancellationToken ct);

    /// <summary>
    /// Delete by identifier (missing — no-op).
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Account repository (no tokens — metadata + TokenRef only).
/// </summary>
public interface IAccountRepository : IRepository<Account>
{
}

/// <summary>
/// Folder-pair repository (the pair keys the run lock and its baseline — invariant).
/// </summary>
public interface IFolderPairRepository : IRepository<FolderPair>
{
}
