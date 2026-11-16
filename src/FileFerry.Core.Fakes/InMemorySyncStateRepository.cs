using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// In-memory <see cref="ISyncStateRepository"/> — behaviorally mirrors the SQLite adapter:
/// journal as an upsert on (pair, seq), pending = entries not in state Applied, sorted by Seq.
/// </summary>
public sealed class InMemorySyncStateRepository : ISyncStateRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<FileSnapshot>> _baselines = new();
    private readonly Dictionary<(Guid Pair, long Seq), JournalEntry> _journal = new();
    private readonly Dictionary<Guid, SyncRunLog> _runs = new();

    /// <inheritdoc cref="ISyncStateRepository.LoadBaselineAsync"/>
    public Task<IReadOnlyList<FileSnapshot>> LoadBaselineAsync(Guid pairId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<FileSnapshot> list = _baselines.TryGetValue(pairId, out var saved) ? saved.ToArray() : [];
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc cref="ISyncStateRepository.ReplaceBaselineAsync"/>
    public Task ReplaceBaselineAsync(Guid pairId, IReadOnlyList<FileSnapshot> baseline, CancellationToken ct)
    {
        lock (_gate)
        {
            _baselines[pairId] = baseline.ToList();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc cref="ISyncStateRepository.AppendJournalAsync"/>
    public Task AppendJournalAsync(Guid pairId, JournalEntry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            _journal[(pairId, entry.Seq)] = entry;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc cref="ISyncStateRepository.LoadJournalAsync"/>
    public Task<IReadOnlyList<JournalEntry>> LoadJournalAsync(Guid pairId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<JournalEntry> list = _journal
                .Where(kv => kv.Key.Pair.Equals(pairId))
                .Select(static kv => kv.Value)
                .OrderBy(static e => e.Seq)
                .ToArray();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc cref="ISyncStateRepository.ClearJournalAsync"/>
    public Task ClearJournalAsync(Guid pairId, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var key in _journal.Keys.Where(k => k.Pair.Equals(pairId)).ToArray())
            {
                _journal.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc cref="ISyncStateRepository.SaveRunLogAsync"/>
    public Task SaveRunLogAsync(SyncRunLog runLog, CancellationToken ct)
    {
        lock (_gate)
        {
            _runs[runLog.RunId] = runLog;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc cref="ISyncStateRepository.RecentRunsAsync"/>
    public Task<IReadOnlyList<SyncRunLog>> RecentRunsAsync(Guid pairId, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<SyncRunLog> list = _runs.Values
                .Where(r => r.PairId.Equals(pairId))
                .OrderByDescending(static r => r.StartedAt)
                .Take(limit)
                .ToArray();
            return Task.FromResult(list);
        }
    }
}
