namespace FileFerry.App.Services;

/// <summary>
/// Normalization of the server address (welcome screen): "parse, don't validate" — only a valid
/// absolute http(s) URL without a trailing '/' ever comes out; an invalid address cannot be
/// built. The default scheme is <c>https</c>: cleartext exists only where the user typed it
/// and confirmed the warning (the caller decides that part, this class only tells the two apart).
/// </summary>
public static class ServerUrl
{
    /// <summary>
    /// Parse raw user input; false — show an inline error.
    /// </summary>
    public static bool TryNormalize(string? raw, out Uri baseUrl)
    {
        baseUrl = null!;
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text; // encrypted by default; http must be typed in full
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || string.IsNullOrEmpty(parsed.Host)
            || parsed.Scheme is not ("http" or "https"))
        {
            return false;
        }

        baseUrl = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/'), UriKind.Absolute);
        return true;
    }

    /// <summary>
    /// Whether the raw input named a scheme itself (then the user meant it); a bare address
    /// got the default and may later be offered the cleartext fallback.
    /// </summary>
    public static bool HasExplicitScheme(string? raw) =>
        (raw ?? string.Empty).Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// The same address under another scheme. An explicitly typed port survives the move;
    /// an implicit one follows the new scheme's default.
    /// </summary>
    public static Uri WithScheme(Uri baseUrl, string scheme)
    {
        var builder = new UriBuilder(baseUrl) { Scheme = scheme };
        if (baseUrl.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }
}
