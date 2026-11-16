using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// In-memory implementation of <see cref="ISecretStore"/> for tests: the same
/// semantic guarantees as the Android adapter — overwrite by key, removal, <c>null</c> for
/// an unknown key. It exists only in the test assembly, so a "secret in memory" does not
/// break the production invariant.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    /// <summary>
    /// Current keys (never values) — for test assertions.
    /// </summary>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_gate)
            {
                return _secrets.Keys.ToList();
            }
        }
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return Task.FromResult(_secrets.GetValueOrDefault(key));
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _secrets[key] = value;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            _secrets.Remove(key); // unknown key is a no-op, like a cascading delete
        }

        return Task.CompletedTask;
    }
}
