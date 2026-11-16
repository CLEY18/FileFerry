using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Domain;

/// <summary>
/// Entry type in the file tree — file or directory (shared by the local and remote sides).
/// </summary>
public enum EntryKind
{
    /// <summary>
    /// Regular file with content.
    /// </summary>
    File,

    /// <summary>
    /// Directory (folder); its content is not readable as a stream.
    /// </summary>
    Directory,
}

/// <summary>
/// Normalized state snapshot of an entry (glossary): everything DiffEngine needs for
/// three-way comparison. Streams/content never go — the snapshot is lightweight and comparable.
/// </summary>
/// <param name="Path">Path relative to the pair root (one coordinate system for local and remote).</param>
/// <param name="Kind">File or directory; a kind mismatch fails content comparison outright.</param>
/// <param name="Size">Size in bytes; 0 for a directory.</param>
/// <param name="ModifiedAt">Modification time when the source provides it (mtime — a hint, not truth).</param>
/// <param name="ETag">Remote version (HEAD/PROPFIND); usually <see langword="null"/> for local files.</param>
/// <param name="ContentHash">Content hash from <see cref="Ports.IHashProvider"/>; computed lazily, only when needed.</param>
public sealed record FileSnapshot(
    RelativePath Path,
    EntryKind Kind,
    long Size,
    DateTimeOffset? ModifiedAt = null,
    string? ETag = null,
    string? ContentHash = null)
{
    /// <summary>
    /// Kind shortcut: a directory has Size 0 and no readable content stream.
    /// </summary>
    public bool IsDirectory => Kind == EntryKind.Directory;

    /// <summary>
    /// Whether content is the same per available evidence, in priority order: ContentHash → ETag → Size+mtime
    /// ("size + ETag, not just mtime"). In the weak branch, mtime is compared
    /// only as a signal of <b>the same coordinate side</b> (both snapshots from local or both from remote —
    /// the baseline row inherits the side's mtime); cross-side "same?" is only <see cref="SameByStrongEvidence"/>.
    /// </summary>
    /// <param name="other">Snapshot to compare with (null — always "no").</param>
    public bool SameContentAs(FileSnapshot? other)
    {
        if (other is null || Kind != other.Kind || Size != other.Size)
        {
            return false;
        }

        if (ContentHash is not null && other.ContentHash is not null)
        {
            return string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);
        }

        if (ETag is not null && other.ETag is not null)
        {
            return string.Equals(ETag, other.ETag, StringComparison.Ordinal);
        }

        if (ModifiedAt is null || other.ModifiedAt is null)
        {
            return true; // bare size — all there is; weak evidence intended for Diff weak branches
        }

        return ModifiedAt.Value.ToUnixTimeSeconds() == other.ModifiedAt.Value.ToUnixTimeSeconds(); // DAV/SAF granularity is one second
    }

    /// <summary>
    /// Strong evidence of content identity: hash on both sides or ETag on both sides.
    /// Bare size/mtime is <b>not</b> strong evidence: silently merging on it alone is forbidden.
    /// </summary>
    public bool SameByStrongEvidence(FileSnapshot? other)
    {
        if (other is null || Kind != other.Kind)
        {
            return false;
        }

        if (ContentHash is not null && other.ContentHash is not null)
        {
            return string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);
        }

        return ETag is not null && other.ETag is not null
            && Size == other.Size
            && string.Equals(ETag, other.ETag, StringComparison.Ordinal);
    }
}
