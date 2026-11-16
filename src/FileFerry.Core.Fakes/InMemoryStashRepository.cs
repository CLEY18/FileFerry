using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// In-memory <see cref="IStashRepository"/> for run tests (TTL-purge, restore). Read
/// order is hottest-first, same as the SQLite adapter.
/// </summary>
public sealed class InMemoryStashRepository : IStashRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Pair, string Path), StashEntry> _entries = new();

    /// <inheritdoc />
    public Task AddAsync(StashEntry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            _entries[(entry.PairId, entry.StashPath.ToString())] = entry;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StashEntry>> ListAsync(Guid pairId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<StashEntry> list = _entries.Values
                .Where(e => e.PairId == pairId)
                .OrderByDescending(static e => e.CreatedAt)
                .ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(Guid pairId, RelativePath stashPath, CancellationToken ct)
    {
        lock (_gate)
        {
            _entries.Remove((pairId, stashPath.ToString()));
        }

        return Task.CompletedTask;
    }
}
