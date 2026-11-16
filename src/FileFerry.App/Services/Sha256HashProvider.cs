using System.Security.Cryptography;
using FileFerry.Core.Ports;

namespace FileFerry.App.Services;

/// <summary>
/// The real <see cref="IHashProvider"/>: SHA-256 as lowercase hex — the same algorithm as the
/// tests' deterministic fake, so baseline evidence has the same shape on the device and in tests.
/// Both sides are hashed independently (local stream / remote stream), so the server's MD5 tag is
/// not needed.
/// </summary>
public sealed class Sha256HashProvider : IHashProvider
{
    /// <inheritdoc cref="IHashProvider.Hash"/>
    public string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <inheritdoc cref="IHashProvider.HashAsync"/>
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
