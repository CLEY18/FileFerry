using System.Text;
using System.Text.RegularExpressions;

namespace FileFerry.Core.Tests.Localization;

/// <summary>
/// The App string resources as tests see them: <c>AppStrings.resx</c> holds the neutral (fallback)
/// text, <c>AppStrings.&lt;culture&gt;.resx</c> is one satellite per supported locale. Locales are
/// discovered from the folder, so a new one is covered by every rule without editing a test.
/// </summary>
internal static class AppStringsFiles
{
    private static readonly Regex SatelliteName = new(@"^AppStrings\.(?<culture>[a-z]{2}(?:-[A-Za-z]{2,4})?)\.resx$", RegexOptions.Compiled);

    private static readonly Regex Entry =
        new(@"<data name=""(?<key>[^""]+)""[^>]*><value>(?<value>.*?)</value>", RegexOptions.Compiled | RegexOptions.Singleline);

    public static string Root { get; } = FindSolutionRoot();

    public static string Folder => Path.Combine(Root, "src", "FileFerry.App", "Resources");

    /// <summary>The neutral file: text of the base language, used when no satellite matches.</summary>
    public static string Neutral => Path.Combine(Folder, "AppStrings.resx");

    /// <summary>Locale id → its <c>key = value</c> map; the neutral file is keyed by the empty string.</summary>
    public static Dictionary<string, Dictionary<string, string>> AllLocales()
    {
        var locales = Directory.EnumerateFiles(Folder, "AppStrings.*.resx")
            .Select(path => (Culture: SatelliteName.Match(Path.GetFileName(path)), Path: path))
            .Where(pair => pair.Culture.Success)
            .ToDictionary(pair => pair.Culture.Groups["culture"].Value, pair => Entries(pair.Path), StringComparer.OrdinalIgnoreCase);

        locales[string.Empty] = Entries(Neutral);
        return locales;
    }

    public static Dictionary<string, string> Entries(string path) =>
        Entry.Matches(File.ReadAllText(path)).ToDictionary(
            match => match.Groups["key"].Value,
            match => match.Groups["value"].Value,
            StringComparer.Ordinal);

    /// <summary>Accessors of <c>AppStrings</c> (each key is either an accessor or a render-time phrase key).</summary>
    public static HashSet<string> AccessorNames() =>
        File.ReadAllLines(Path.Combine(Folder, "AppStrings.cs"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("public static string ", StringComparison.Ordinal)
                           && line.EndsWith("=> Get();", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[3])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Letters beyond Basic Latin — the marker that text left the base language.</summary>
    public static bool IsNonAsciiLetter(char ch) => ch > 127 && char.IsLetter(ch);

    /// <summary>
    /// Pictograph by code point: the symbol, dingbat and pictographic planes of Unicode. Arrows, box
    /// drawing, block shades, geometric shapes and technical symbols draw diagrams rather than depict
    /// something, so they are not counted here. A text element beyond the Basic Multilingual Plane is
    /// two UTF-16 cells, so it is read as a <see cref="Rune"/>, never as a single <c>char</c>.
    /// </summary>
    public static bool IsEmoji(int codePoint) =>
        codePoint is >= 0x1F000 and <= 0x1FAFF
        || codePoint is >= 0x2600 and <= 0x27BF
        || codePoint is >= 0x2B00 and <= 0x2BFF
        || codePoint is >= 0xFE00 and <= 0xFE0F
        || codePoint == 0x200D || codePoint == 0x00A9 || codePoint == 0x00AE;

    /// <summary>Whether a decoded line of text holds a pictograph.</summary>
    public static bool HasEmoji(string text) => text.EnumerateRunes().Any(rune => IsEmoji(rune.Value));

    /// <summary>Predicate over a code-point range — how a locale declares the script of its values.</summary>
    public static Func<char, bool> UnicodeRange(char from, char to) => ch => ch >= from && ch <= to;

    public static IEnumerable<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+").Select(match => match.Value);

    public static string Relative(string path) =>
        Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FileFerry.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("FileFerry.sln not found above the test assembly.");
    }
}
