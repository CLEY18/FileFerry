using FileFerry.Client.WebDav;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// Fixture units for <see cref="MultistatusParser"/> on recorded Nextcloud 207 responses:
/// Unicode hrefs, the collection's self entry, query trimming, 200-propstat selection, rotten entries.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MultistatusParserTests
{
    private const string Prefix = "/remote.php/dav/files/anna/";

    /// <summary>
    /// Realistic Depth-1 response on the folder-pair root: self entry, a directory whose name is
    /// written in another script ("Rozdil"), a PDF with a query, a file with a 404-propstat.
    /// </summary>
    private const string RootListing = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns" xmlns:oc="http://owncloud.org/ns">
          <d:response>
            <d:href>/remote.php/dav/files/anna/sync/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/></d:resourcetype>
                <d:getetag>"abc123"</d:getetag>
                <d:getlastmodified>Wed, 05 Nov 2025 10:15:30 GMT</d:getlastmodified>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/anna/sync/%D0%A0%D0%BE%D0%B7%D0%B4%D1%96%D0%BB/</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype><d:collection/></d:resourcetype>
                <d:getetag>"def456"</d:getetag>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/anna/sync/report%202026.pdf?fileid=4213</d:href>
            <d:propstat>
              <d:prop>
                <d:resourcetype/>
                <d:getetag>"7f3a-65f"</d:getetag>
                <d:getcontentlength>20485</d:getcontentlength>
                <d:getlastmodified>Tue, 04 Nov 2025 08:00:00 GMT</d:getlastmodified>
              </d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
          <d:response>
            <d:href>/remote.php/dav/files/anna/sync/plain.txt</d:href>
            <d:propstat>
              <d:prop><d:getcontentlength/></d:prop>
              <d:status>HTTP/1.1 404 Not Found</d:status>
            </d:propstat>
            <d:propstat>
              <d:prop><d:resourcetype/><d:getetag>"p1"</d:getetag><d:getcontentlength>10</d:getcontentlength></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status>
            </d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public void Parse_Listing_MapsEveryResponseShape()
    {
        var parsed = MultistatusParser.Parse(RootListing, Prefix);

        Assert.True(parsed.IsSuccess);
        var byPath = parsed.Value.ToDictionary(static e => e.Path.ToString());

        Assert.Equal(4, byPath.Count);
        Assert.Equal(EntryKind.Directory, byPath["sync"].Kind); // the self entry is present too — the scanner filters it out
        Assert.Equal("\"abc123\"", byPath["sync"].ETag);

        var section = byPath["sync/Розділ"]; // href percent-encoded → decoded into RemotePath
        Assert.Equal(EntryKind.Directory, section.Kind);
        Assert.Equal("\"def456\"", section.ETag);

        var pdf = byPath["sync/report 2026.pdf"]; // "?fileid=" is not part of the path
        Assert.Equal(EntryKind.File, pdf.Kind);
        Assert.Equal(20485, pdf.Size);
        Assert.Equal(new DateTimeOffset(2025, 11, 4, 8, 0, 0, TimeSpan.Zero), pdf.ModifiedAt);

        Assert.Equal("\"p1\"", byPath["sync/plain.txt"].ETag); // 404-propstat ignored, the 200 one taken
        Assert.Equal(10, byPath["sync/plain.txt"].Size);
    }

    [Fact]
    public void Parse_HrefOutsidePrefix_FailsWithUnknown()
    {
        var xml = RootListing.Replace("/remote.php/dav/files/anna/", "/remote.php/dav/files/bogdan/");

        var parsed = MultistatusParser.Parse(xml, Prefix);

        Assert.True(parsed.IsFailure);
        Assert.Equal(ErrorKind.Unknown, parsed.Error.Kind); // a foreign tree — configuration error, not emptiness
    }

    [Fact]
    public void Parse_HrefOfNeighbouringAccount_FailsWithUnknown()
    {
        // the prefix trims to ".../anna": ".../anna2/..." is somebody else's tree and may not
        // join this pair's namespace just because the two names start alike
        var xml = $$"""
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/remote.php/dav/files/anna2/x.txt</d:href>
            <d:propstat><d:prop><d:getetag>"n1"</d:getetag></d:prop>
            <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
            """;

        var parsed = MultistatusParser.Parse(xml, Prefix);

        Assert.True(parsed.IsFailure);
        Assert.Equal(ErrorKind.Unknown, parsed.Error.Kind);
    }

    [Fact]
    public void Parse_MalformedXml_FailsWithUnknown()
    {
        var parsed = MultistatusParser.Parse("<d:multistatus><d:response>", Prefix);

        Assert.True(parsed.IsFailure);
        Assert.Equal(ErrorKind.Unknown, parsed.Error.Kind);
    }

    [Fact]
    public void Parse_EmptyMultistatus_YieldsNoEntries()
    {
        var parsed = MultistatusParser.Parse("""<d:multistatus xmlns:d="DAV:"/>""", Prefix);

        Assert.True(parsed.IsSuccess);
        Assert.Empty(parsed.Value);
    }

    [Fact]
    public void Parse_RootHref_MapsToRootPath()
    {
        var xml = $$"""
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>{{Prefix}}</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
            <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
            """;

        var parsed = MultistatusParser.Parse(xml, Prefix);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(RemotePath.Root, parsed.Value.Single().Path);
    }
}
