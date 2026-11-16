namespace FileFerry.Core.Domain.Paths;

/// <summary>
/// Value object: a relative path in the user's file root on the server,
/// i.e. the part of a DAV-href after <c>/remote.php/dav/files/&lt;user&gt;/</c>.
/// Unlike <see cref="RelativePath"/>, input is usually percent-encoded (from PROPFIND/href),
/// so <see cref="Parse"/> decodes escapes first, then applies the same canonicalization
/// (NFC, no <c>..</c>). Encoding for HTTP requests is <see cref="ToEncodedPath"/>;
/// the object itself compares in decoded form.
/// </summary>
public sealed record RemotePath : IComparable<RemotePath>
{
    /// <summary>
    /// Canonical decoded text: segments joined by <c>'/'</c>; root is "".
    /// </summary>
    private readonly string _value;

    /// <summary>
    /// Construction only from normalized text (internal to the type plumbing).
    /// </summary>
    private RemotePath(string normalizedValue) => _value = normalizedValue;

    /// <summary>
    /// The user's file root (the adapter turns it into an href with a terminal <c>'/'</c>).
    /// </summary>
    public static RemotePath Root { get; } = new(string.Empty);

    /// <summary>
    /// Parses a path or DAV-href tail: percent-decode → NFC → canonicalization; <c>%2E%2E</c> is rejected too.
    /// </summary>
    /// <param name="path">Raw (possibly encoded) path; a leading <c>'/'</c> is allowed.</param>
    public static RemotePath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathText.TryNormalize(path, percentDecode: true, out var value)
            ? new RemotePath(value)
            : throw new FormatException($"Remote path is impossible: '{path}' (escapes the root or contains control characters).");
    }

    /// <summary>
    /// Parses already-decoded text (values inside PROPFIND XML are not encoded): canonicalization without percent-decode.
    /// </summary>
    public static RemotePath ParseDecoded(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathText.TryNormalize(path, percentDecode: false, out var value)
            ? new RemotePath(value)
            : throw new FormatException($"Remote path is impossible: '{path}'.");
    }

    /// <summary>
    /// Tries to parse a path without throwing (boundary with network data).
    /// </summary>
    /// <param name="path">Raw path.</param>
    /// <param name="result">Parsed path or <see langword="null"/>.</param>
    public static bool TryParse(string? path, out RemotePath? result)
    {
        result = PathText.TryNormalize(path, percentDecode: true, out var value)
            ? new RemotePath(value)
            : null;
        return result is not null;
    }

    /// <summary>
    /// Whether this is the user's file root.
    /// </summary>
    public bool IsRoot => _value.Length == 0;

    /// <summary>
    /// Last segment (name); empty string for the root.
    /// </summary>
    public string Name
    {
        get
        {
            var slash = _value.LastIndexOf(PathText.Separator);
            return slash < 0 ? _value : _value[(slash + 1)..];
        }
    }

    /// <summary>
    /// Parent path; the root itself for the root and for single-level paths.
    /// </summary>
    public RemotePath Parent
    {
        get
        {
            var slash = _value.LastIndexOf(PathText.Separator);
            return slash < 0 ? Root : new RemotePath(_value[..slash]);
        }
    }

    /// <summary>
    /// Segments of the decoded path (empty array for the root).
    /// </summary>
    public IReadOnlyList<string> Segments =>
        _value.Length == 0 ? [] : _value.Split(PathText.Separator);

    /// <summary>
    /// Segment count; root is 0.
    /// </summary>
    public int Depth => IsRoot ? 0 : 1 + _value.Count(static c => c == PathText.Separator);

    /// <summary>
    /// Key for case-insensitive matching on the remote side.
    /// </summary>
    public string Key => _value.ToLowerInvariant();

    /// <summary>
    /// Path + child path (percent-escapes in the child are decoded).
    /// </summary>
    public RemotePath Combine(string child) => Parse(_value + "/" + child);

    /// <summary>
    /// Path + another path (canonicality preserved: an empty child ≠ a trailing "/" tail).
    /// </summary>
    public RemotePath Combine(RelativePath child) => AppendChild(new RemotePath(child.ToString()));

    /// <summary>
    /// Path + another canonical path by direct append — no percent-decode, no re-parse;
    /// an empty child yields this path unchanged.
    /// </summary>
    public RemotePath Combine(RemotePath child) => AppendChild(child);

    /// <summary>
    /// Appends an already-canonical decoded descendant without re-parsing.
    /// </summary>
    private RemotePath AppendChild(RemotePath child) =>
        child.IsRoot ? this : IsRoot ? child : new RemotePath(_value + "/" + child._value);

    /// <summary>
    /// Whether this path lies under <paramref name="prefix"/> (segment-wise prefix).
    /// </summary>
    /// <param name="prefix">Potential ancestor or the path itself.</param>
    public bool StartsWith(RemotePath prefix) =>
        prefix.IsRoot || (_value + PathText.Separator).StartsWith(prefix._value + PathText.Separator, StringComparison.Ordinal);

    /// <summary>
    /// Case-insensitive comparison (case-collision on the server side).
    /// </summary>
    public bool EqualsIgnoreCase(RemotePath other) =>
        string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Path for HTTP requests: each segment percent-escaped separately (RFC 3986), joined with <c>'/'</c>.
    /// </summary>
    public string ToEncodedPath() =>
        string.Join(PathText.Separator, Segments.Select(Uri.EscapeDataString));

    /// <summary>
    /// Path relative to <paramref name="root"/> (for the pair's Diff coordinates); outside the subroot — <see cref="ArgumentException"/>.
    /// </summary>
    /// <param name="root">Root prefix that this path starts with.</param>
    public RelativePath RelativeTo(RemotePath root)
    {
        if (!StartsWith(root))
        {
            throw new ArgumentException($"The path '{this}' does not live under the root '{root}'.");
        }

        if (_value.Length == root._value.Length)
        {
            return RelativePath.Empty; // the root itself → empty pair path
        }

        var offset = root.IsRoot ? 0 : root._value.Length + 1;
        return RelativePath.Parse(_value[offset..]);
    }

    /// <summary>
    /// Order: by depth, then ordinal (same ordering as <see cref="RelativePath"/>).
    /// </summary>
    public int CompareTo(RemotePath? other) => other is null
        ? 1
        : Depth != other.Depth
            ? Depth.CompareTo(other.Depth)
            : string.CompareOrdinal(_value, other._value);

    /// <summary>
    /// Canonical decoded path text.
    /// </summary>
    public override string ToString() => _value;
}
