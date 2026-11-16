namespace FileFerry.App.Services;

/// <summary>
/// Normalization of the server address (welcome screen): "parse, don't validate" — only a valid
/// absolute http(s) URL without a trailing '/' ever comes out; an invalid address cannot be
/// built. The default scheme is <c>http</c> (LAN); <c>https</c> is preserved as is.
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
            text = "http://" + text; // http default for LAN servers
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
}
