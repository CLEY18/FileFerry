namespace FileFerry.Core.Domain.Paths;

/// <summary>
/// Value object: a relative path inside a sync pair.
/// The constructor is private — instances come only from <see cref="Parse"/>/combinators,
/// so invalid states (<c>..</c>, <c>//</c>, NFD duplicates, empty segments) are impossible
/// (parse-don't-validate). Equality is ordinal and case-sensitive; for "possibly the same
/// file" matching there is <see cref="Key"/>.
/// </summary>
public sealed record RelativePath : IComparable<RelativePath>
{
    /// <summary>
    /// Canonical text: segments joined by <c>'/'</c>, no leading/trailing separator; root is "".
    /// </summary>
    private readonly string _value;

    /// <summary>
    /// Construction only from normalized text (internal to the type plumbing).
    /// </summary>
    private RelativePath(string normalizedValue) => _value = normalizedValue;

    /// <summary>
    /// The pair root — neutral element of combination.
    /// </summary>
    public static RelativePath Empty { get; } = new(string.Empty);

    /// <summary>
    /// Parses and normalizes a path; throws <see cref="FormatException"/> on impossible input.
    /// </summary>
    /// <param name="path">Raw path; a leading <c>'/'</c> is allowed and dropped.</param>
    public static RelativePath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return PathText.TryNormalize(path, percentDecode: false, out var value)
            ? new RelativePath(value)
            : throw new FormatException($"Path is impossible: '{path}' (escapes the root or contains control characters).");
    }

    /// <summary>
    /// Tries to parse a path (boundary with user input); returns <see langword="false"/> without throwing.
    /// </summary>
    /// <param name="path">Raw path.</param>
    /// <param name="result">Parsed path or <see langword="null"/>.</param>
    public static bool TryParse(string? path, out RelativePath? result)
    {
        result = PathText.TryNormalize(path, percentDecode: false, out var value)
            ? new RelativePath(value)
            : null;
        return result is not null;
    }

    /// <summary>
    /// Whether this is the pair root.
    /// </summary>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>
    /// Last segment (file/folder name); empty string for the root.
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
    public RelativePath Parent
    {
        get
        {
            var slash = _value.LastIndexOf(PathText.Separator);
            return slash < 0 ? Empty : new RelativePath(_value[..slash]);
        }
    }

    /// <summary>
    /// Segments of the canonical path (empty array for the root).
    /// </summary>
    public IReadOnlyList<string> Segments =>
        _value.Length == 0 ? [] : _value.Split(PathText.Separator);

    /// <summary>
    /// Segment count; root is 0. Used for topological ordering of operations.
    /// </summary>
    public int Depth => IsEmpty ? 0 : 1 + _value.Count(static c => c == PathText.Separator);

    /// <summary>
    /// Key for case-insensitive matching (case-collision detection).
    /// </summary>
    public string Key => _value.ToLowerInvariant();

    /// <summary>
    /// Path + child relative path (normalized together; <c>..</c> in the child throws).
    /// </summary>
    /// <param name="child">Child path; may have a leading <c>'/'</c>.</param>
    public RelativePath Combine(string child) => Parse(_value + "/" + child);

    /// <summary>
    /// Path + an already-parsed child; both sides are normalized, so nothing can throw here.
    /// </summary>
    /// <param name="child">Child path; <see cref="Empty"/> returns this path unchanged.</param>
    public RelativePath Combine(RelativePath child) => Parse(_value + "/" + child._value);

    /// <summary>
    /// Whether this path lies under <paramref name="prefix"/> (SEGMENT-wise prefix: "a/bc" is not under "a/b").
    /// </summary>
    /// <param name="prefix">Potential ancestor or the path itself.</param>
    public bool StartsWith(RelativePath prefix) =>
        prefix.IsEmpty || (_value + PathText.Separator).StartsWith(prefix._value + PathText.Separator, StringComparison.Ordinal);

    /// <summary>
    /// Case-insensitive comparison — raw material for the case-collision scenario.
    /// </summary>
    public bool EqualsIgnoreCase(RelativePath other) =>
        string.Equals(_value, other._value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Order: depth first (parents earlier), then ordinal — stable Diff/Planner output.
    /// </summary>
    public int CompareTo(RelativePath? other) => other is null
        ? 1
        : Depth != other.Depth
            ? Depth.CompareTo(other.Depth)
            : string.CompareOrdinal(_value, other._value);

    /// <summary>
    /// Canonical path text.
    /// </summary>
    public override string ToString() => _value;
}
