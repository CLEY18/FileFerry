using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Tests.Domain.Paths;

/// <summary>
/// Table-driven unit tests for <see cref="RemotePath"/>: percent-decoded
/// hrefs, traversal safety (<c>%2E%2E</c>), encoding round-trip, NFC, navigation.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RemotePathTests
{
    [Theory]
    [InlineData("", true)]                  // empty — root
    [InlineData("/", true)]
    [InlineData("/a", false)]
    [InlineData("a b/c", false)]
    public void Root_Detection(string input, bool isRoot) =>
        Assert.Equal(isRoot, RemotePath.Parse(input).IsRoot);

    [Theory]
    [InlineData("Documents/My%20File.txt", "Documents/My File.txt")]  // decodes a typical href
    [InlineData("%D0%90%D0%B1/%D0%92", "Аб/В")]                        // UTF-8 percent-encoding → NFC text
    [InlineData("a/./b//c/", "a/b/c")]                                 // same canonicalization as in RelativePath
    [InlineData("e\u0301", "\u00e9")]                                  // NFC here too
    public void Parse_DecodesAndNormalizes(string input, string expected) =>
        Assert.Equal(expected, RemotePath.Parse(input).ToString());

    [Theory]
    [InlineData("a/%2E%2E/b")]              // encoded ".." still escapes the root
    [InlineData("%2e%2e")]
    [InlineData("a/..")]
    [InlineData("x%00y")]                   // control character after decode
    public void Parse_EscapedTraversal_Throws(string input) =>
        Assert.Throws<FormatException>(() => RemotePath.Parse(input));

    [Theory]
    [InlineData("a b/c")]
    [InlineData("Docs/résumé.txt")]
    [InlineData("100% & 50%")]              // a literal '%' survives the round-trip
    [InlineData("q?x")]                     // reserved characters get escaped
    [InlineData("w#frag")]
    [InlineData("s(1).txt")]
    [InlineData("a\\b")]                    // '\' is an ordinary name character
    public void EncodedPath_RoundTripsThroughParse(string original)
    {
        var encoded = RemotePath.Parse(original).ToEncodedPath();
        Assert.Equal(original, RemotePath.Parse(encoded).ToString());
        Assert.DoesNotContain(' ', encoded);
        Assert.DoesNotContain('?', encoded);
    }

    [Fact]
    public void EncodedPath_IsPercentEscapedPerSegment() =>
        Assert.Equal("Documents/My%20File%231.txt", RemotePath.Parse("/Documents/My File#1.txt").ToEncodedPath());

    [Fact]
    public void Navigation_MirrorsRelativePath()
    {
        var path = RemotePath.Parse("/remote.php/Docs/a%20b.txt");
        Assert.Equal("a b.txt", path.Name);
        Assert.Equal(RemotePath.Parse("remote.php/Docs"), path.Parent);
        Assert.True(path.StartsWith(RemotePath.Parse("remote.php")));
        Assert.False(path.StartsWith(RemotePath.Parse("remote")));
    }

    [Theory]
    [InlineData("", "b", "b")]
    [InlineData("a", "b%20c", "a/b c")]
    [InlineData("a", "", "a")]              // an empty child adds no trailing "/"
    public void Combine_DecodesChild(string left, string right, string expected) =>
        Assert.Equal(expected, RemotePath.Parse(left).Combine(right).ToString());

    [Fact]
    public void Combine_TypedOverloads_KeepCanonicalForm()
    {
        var pair = RemotePath.Root.Combine(RelativePath.Parse("Docs/sub"));
        Assert.Equal("Docs/sub", pair.ToString());
        Assert.Equal("Docs/sub", pair.Combine(RemotePath.Root).ToString());
        Assert.Equal("Docs/sub", RemotePath.Parse("Docs").Combine(RelativePath.Parse("sub")).ToString());
    }

    [Fact]
    public void CrossMapping_RemoteAndRelativeCompareAsText() =>
        // the key property for DiffEngine: the same relative path from both sides
        Assert.Equal(
            RelativePath.Parse("Docs/файл.txt").ToString(),
            RemotePath.Parse("Docs/%D1%84%D0%B0%D0%B9%D0%BB.txt").ToString());
}
