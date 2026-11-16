using System.Security.Cryptography;
using FileFerry.Core.Ports;

namespace FileFerry.Core.Fakes;

/// <summary>
/// Time that only tests advance: fixed start 2026-01-01T00:00Z plus explicit
/// <see cref="Advance"/> calls.
/// </summary>
public sealed class MutableClock : IClock
{
    /// <summary>
    /// Current "virtual" instant; changes only via <see cref="Advance"/>/<see cref="Set"/>.
    /// </summary>
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Shift time forward (for stash TTL and backoff timing). A negative delta moves time
    /// back, which is of no use in tests.
    /// </summary>
    public void Advance(TimeSpan delta) => UtcNow += delta;

    /// <summary>
    /// Set the clock to a specific UTC instant.
    /// </summary>
    public void Set(DateTimeOffset value) => UtcNow = value;
}

/// <summary>
/// Deterministic hash adapter: real SHA-256, the same algorithm the App uses.
/// </summary>
public sealed class Sha256HashProvider : IHashProvider
{
    /// <summary>
    /// Lowercase hex SHA-256.
    /// </summary>
    public string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// SHA-256 of a stream (incremental, without buffering it all in memory).
    /// </summary>
    public async Task<string> HashAsync(Stream content, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
