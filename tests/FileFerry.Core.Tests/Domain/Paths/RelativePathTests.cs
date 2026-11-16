using FileFerry.Core.Domain.Paths;

namespace FileFerry.Core.Tests.Domain.Paths;

/// <summary>
/// Table-driven unit tests for <see cref="RelativePath"/> normalization:
/// separators, ".", "..", NFC/NFD, case, navigation, combining.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RelativePathTests
{
    [Theory]
    [InlineData("", "")]                    // empty — root
    [InlineData("/", "")]                   // separator only — root
    [InlineData("///", "")]
    [InlineData("a", "a")]
    [InlineData("/a/", "a")]                // leading/trailing separators are dropped
    [InlineData("a//b", "a/b")]             // repeated separators collapse
    [InlineData("a/./b", "a/b")]            // "." segments are dropped
    [InlineData("./a/.", "a")]
    [InlineData(" a /b ", "a /b")]          // trim applies to the whole string only; inner spaces are data
    [InlineData("e\u0301.txt", "\u00e9.txt")]            // NFD → NFC
    [InlineData("\u0041\u0308/x", "\u00C4/x")]           // "A" + a combining mark composes into U+00C4
    [InlineData("ü/ä\u0308", "\u00fc/\u00e4\u0308")]     // NFC never re-composes what is already composed
    public void Parse_Normalizes(string input, string expected) =>
        Assert.Equal(expected, RelativePath.Parse(input).ToString());

    [Theory]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData("a/../b")]
    [InlineData("/..")]
    [InlineData("..\r")]                    // control character
    [InlineData("a\u0000b")]                // NUL
    [InlineData("a\nb/c")]
    public void Parse_ImpossiblePath_Throws(string input) =>
        Assert.Throws<FormatException>(() => RelativePath.Parse(input));

    [Fact]
    public void Parse_Null_ThrowsArgumentNull() =>
        Assert.Throws<ArgumentNullException>(() => RelativePath.Parse(null!));

    [Theory]
    [InlineData("a/..", false)]
    [InlineData("a/b", true)]
    [InlineData(null, false)]
    public void TryParse_ReportsWithoutThrow(string? input, bool ok)
    {
        Assert.Equal(ok, RelativePath.TryParse(input, out var path));
        Assert.Equal(ok, path is not null);
    }

    [Theory]
    [InlineData("a//b", "a/b")]             // equality is by canonical text
    [InlineData("a", "a/")]
    public void Parse_EquivalentTexts_AreEqual(string left, string right) =>
        Assert.Equal(RelativePath.Parse(left), RelativePath.Parse(right));

    [Fact]
    public void Equality_IsCaseSensitive_ButKeyIsNot()
    {
        var upper = RelativePath.Parse("Docs/Readme.md");
        var lower = RelativePath.Parse("docs/readme.MD");

        Assert.NotEqual(upper, lower);                       // "A" and "a" are different files
        Assert.True(upper.EqualsIgnoreCase(lower));          // case-collision signal
        Assert.Equal(upper.Key, lower.Key);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("a/b/c", "c")]
    public void Name_LastSegment(string input, string expected) =>
        Assert.Equal(expected, RelativePath.Parse(input).Name);

    [Theory]
    [InlineData("", "")]
    [InlineData("a", "")]
    [InlineData("a/b/c", "a/b")]
    public void Parent_DropsLastSegment(string input, string expected) =>
        Assert.Equal(RelativePath.Parse(expected), RelativePath.Parse(input).Parent);

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a/b/c", 3)]
    public void Depth_CountsSegments(string input, int expected) =>
        Assert.Equal(expected, RelativePath.Parse(input).Depth);

    [Theory]
    [InlineData("a/b/c", "a/b", true)]
    [InlineData("a/b/c", "a/b/c", true)]    // a path is a prefix of itself
    [InlineData("a/b/c", "", true)]         // root is a prefix of everything
    [InlineData("a/bc", "a/b", false)]      // prefix by SEGMENTS, not characters
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("b/c", "a/b", false)]
    public void StartsWith_IsSegmentWise(string input, string prefix, bool expected) =>
        Assert.Equal(expected, RelativePath.Parse(input).StartsWith(RelativePath.Parse(prefix)));

    [Theory]
    [InlineData("a", "b", "a/b")]
    [InlineData("", "b", "b")]              // Empty is the identity element
    [InlineData("a", "/b/", "a/b")]         // the child is normalized
    [InlineData("a/b", "c/./d", "a/b/c/d")]
    public void Combine_NormalizesJoined(string left, string right, string expected) =>
        Assert.Equal(expected, RelativePath.Parse(left).Combine(right).ToString());

    [Fact]
    public void Combine_PathOverload_AndRejectsEscape()
    {
        Assert.Equal("a/b", RelativePath.Empty.Combine(RelativePath.Parse("a/b")).ToString());
        Assert.Throws<FormatException>(() => RelativePath.Parse("a").Combine("../x"));
    }

    [Fact]
    public void Ordering_ParentsBeforeChildren_StableForOutput()
    {
        var paths = new[] { "a/b", "b", "a", "" }
            .Select(RelativePath.Parse)
            .Order()
            .ToArray();

        Assert.Equal(",a,b,a/b", string.Join(",", paths.Select(static p => p.ToString())));
    }

    [Fact]
    public void Segments_BackslashIsLiteral_NotSeparator() =>
        // Pinned decision: SAF/DAV use '/' as the only separator; '\' is an ordinary character
        Assert.Equal(@"a\b", RelativePath.Parse(@"a\b").Segments.Single());
}
