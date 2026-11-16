using FileFerry.Core.Ports;

namespace FileFerry.App;

/// <summary>
/// Android adapter for <see cref="ISecretStore"/> over <see cref="SecureStorage"/>: on Android the
/// AES key lives in the Keystore and values in encrypted SharedPreferences (invariant:
/// the app token lives only here; the DB holds only a <c>token_ref</c>).
/// </summary>
public sealed class AndroidKeystoreSecretStore : ISecretStore
{
    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        return await SecureStorage.Default.GetAsync(key);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();
        await SecureStorage.Default.SetAsync(key, value);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
