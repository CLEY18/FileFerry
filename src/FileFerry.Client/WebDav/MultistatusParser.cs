using System.Globalization;
using System.Xml.Linq;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Client.WebDav;

/// <summary>
/// Parses <c>207 Multistatus</c> (PROPFIND Depth:0/1) into <see cref="RemoteEntry"/>.
/// Tolerant of namespace "sauces" (d:, no-ns, sabredav) and extra properties; href is taken
/// decoded (so as not to break the NFC composition of names); properties with a 404 status are ignored.
/// </summary>
public static class MultistatusParser
{
    private static readonly XNamespace Dav = "DAV:";

    /// <summary>
    /// Parse the response body into a flat list of entries.
    /// </summary>
    /// <param name="xml">Body of the 207 response.</param>
    /// <param name="hrefPrefix">DAV-path prefix up to the user's namespace, e.g. <c>/remote.php/dav/files/anna/</c>.</param>
    public static Result<IReadOnlyList<RemoteEntry>, SyncError> Parse(string xml, string hrefPrefix)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(
                SyncError.Of(ErrorKind.Unknown, "Multistatus is not valid XML.", ex));
        }

        var entries = new List<RemoteEntry>();
        foreach (var response in doc.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (href is null)
            {
                continue; // a response without href is not a tree entry
            }

            if (!TryRelativeToUser(href, hrefPrefix, out var path, out var isFolder))
            {
                return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(SyncError.Of(
                    ErrorKind.Unknown, $"Multistatus contains an href outside the DAV prefix: '{Truncate(href)}'."));
            }

            var props = SuccessfulProps(response);
            var isCollection = isFolder
                || props.Descendants(Dav + "resourcetype").Any(rt => rt.Element(Dav + "collection") is not null);

            var etag = props.Element(Dav + "getetag")?.Value.Trim();
            var size = long.TryParse(props.Element(Dav + "getcontentlength")?.Value, out var len) ? len : 0;
            var modified = ParseHttpDate(props.Element(Dav + "getlastmodified")?.Value);

            entries.Add(new RemoteEntry(
                path,
                isCollection ? EntryKind.Directory : EntryKind.File,
                isCollection ? 0 : size,
                modified,
                string.IsNullOrEmpty(etag) ? null : etag));
        }

        return Result<IReadOnlyList<RemoteEntry>, SyncError>.Success(entries);
    }

    /// <summary>
    /// href (possibly with a query and a trailing "/") → path relative to the user's DAV root.
    /// </summary>
    private static bool TryRelativeToUser(string href, string hrefPrefix, out RemotePath path, out bool isFolder)
    {
        isFolder = href.EndsWith('/');
        var trimmed = href.TrimEnd('/');
        var query = trimmed.IndexOf('?'); // NC sometimes appends ?fileid=… — not part of the path
        if (query >= 0)
        {
            trimmed = trimmed[..query];
        }

        var decoded = Uri.UnescapeDataString(trimmed);
        var prefix = hrefPrefix.TrimEnd('/');
        if (!decoded.StartsWith(prefix, StringComparison.Ordinal))
        {
            path = RemotePath.Root;
            return false;
        }

        path = RemotePath.ParseDecoded(decoded[prefix.Length..].TrimStart('/'));
        return true;
    }

    /// <summary>
    /// The first <c>propstat</c> with a successful status — its <c>prop</c>; 404 properties are not read.
    /// </summary>
    private static XElement SuccessfulProps(XElement response)
    {
        var stat = response.Elements(Dav + "propstat").FirstOrDefault(p =>
            p.Element(Dav + "status")?.Value.Contains(" 200", StringComparison.Ordinal) ?? true);
        return stat?.Element(Dav + "prop") ?? new XElement(Dav + "prop");
    }

    /// <summary>
    /// HTTP-date (RFC 7231, almost always the "R" format); unparseable → <see langword="null"/>.
    /// </summary>
    private static DateTimeOffset? ParseHttpDate(string? value) =>
        value is not null
        && DateTimeOffset.TryParseExact(value, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    /// <summary>
    /// An href can be long — logs and errors get a truncated one.
    /// </summary>
    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "…";
}
