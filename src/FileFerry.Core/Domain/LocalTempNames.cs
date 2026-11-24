namespace FileFerry.Core.Domain;

/// <summary>
/// The naming rule for interrupted-write fragments. Both local FS adapters write through a
/// temporary name — "&lt;name&gt;.tmp-&lt;32 hex&gt;" on plain storage, ".tmp-&lt;32 hex&gt;" under SAF
/// (the final name arrives at rename time) — and rename into place. A process stopped mid-write
/// leaves such a fragment behind: it is the app's own unfinished transaction, not user content,
/// so it must not sync upward and must not litter the tree forever. The writer, the planner
/// exclusion and the sweep share this one rule so they cannot drift apart.
/// </summary>
public static class LocalTempNames
{
    private const string Marker = ".tmp-";
    private const int IdLength = 32;

    /// <summary>
    /// Temporary name for a plain-filesystem write of <paramref name="finalName"/> (same directory).
    /// </summary>
    public static string ForPlainFile(string finalName) => $"{finalName}{Marker}{Guid.NewGuid():N}";

    /// <summary>
    /// Temporary document name for a SAF write — no name prefix, the final name comes at rename.
    /// </summary>
    public static string ForSafDocument() => $"{Marker}{Guid.NewGuid():N}";

    /// <summary>
    /// Whether <paramref name="fileName"/> has the exact shape of a write fragment: ends with
    /// <c>.tmp-</c> plus 32 hex digits. Names that merely resemble it ("<c>x.tmp-notes</c>")
    /// stay honest sync state — the rule recognizes our litter, not every dotted name.
    /// </summary>
    public static bool IsFragment(string fileName)
    {
        var tail = fileName.Length - (Marker.Length + IdLength);
        if (tail < 0 || !fileName.AsSpan(tail).StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in fileName.AsSpan(tail + Marker.Length))
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
