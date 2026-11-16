using FileFerry.Core.Domain;

namespace FileFerry.Core.Ports;

/// <summary>
/// A remote-tree entry from a PROPFIND reply: address + metadata. The Client adapter builds these
/// from XML; the engine turns them into <see cref="FileSnapshot"/> relative to the pair root.
/// </summary>
/// <param name="Path">Path from the user's DAV root (decoded).</param>
/// <param name="Kind">File or collection; a collection has no readable content stream.</param>
/// <param name="Size">Size in bytes; 0 for a collection.</param>
/// <param name="ModifiedAt">getlastmodified, when the server provided it.</param>
/// <param name="ETag">getetag of the collection/file — engine diff and ETag-pruning.</param>
public sealed record RemoteEntry(
    Domain.Paths.RemotePath Path,
    EntryKind Kind,
    long Size,
    DateTimeOffset? ModifiedAt = null,
    string? ETag = null)
{
    /// <summary>
    /// Snapshot for the pair's Diff coordinates (the path becomes relative to <paramref name="pairRoot"/>).
    /// </summary>
    /// <param name="pairRoot">The pair's remote root.</param>
    public FileSnapshot ToSnapshot(Domain.Paths.RemotePath pairRoot) =>
        new(Path.RelativeTo(pairRoot), Kind, Size, ModifiedAt, ETag);
}
