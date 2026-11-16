using System.Text.RegularExpressions;

namespace FileFerry.Core.Tests.Localization;

/// <summary>
/// Resource conventions: UI text exists only in the string resources — the neutral
/// <c>AppStrings.resx</c> plus one satellite per locale — and text outside those values uses plain
/// ASCII letters. Locales are discovered from the resource folder, so a new one is checked
/// every rule the moment its file appears.
/// </summary>
[Trait("Category", "Unit")]
public class ResourceConventionTests
{
    /// <summary>Values with no language of their own: brand names and a bare format string.</summary>
    private static readonly string[] LanguageFreeKeys = ["AppDisplayName", "ThemeNextcloudLabel", "EventRaw"];

    /// <summary>Script a locale is written — a code-point range per locale: adding a satellite means adding its row.</summary>
    private static readonly Dictionary<string, Func<char, bool>> ExpectedScript = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = AppStringsFiles.UnicodeRange('\u0400', '\u04FF'),
    };

    /// <summary>
    /// Locales whose script is Latin: a value in such a locale is Latin letters by definition, so the
    /// foreign-script rule below has nothing to look for. Every locale that is not listed there is checked.
    /// </summary>
    private static readonly string[] LatinScriptLocales = [];

    /// <summary>
    /// Latin words a localized value legitimately keeps: the brand, the server product, and acronyms
    /// with no shorter everyday form. Anything else of Latin letters inside a foreign-script locale is
    /// an untranslated intruder, not a term.
    /// </summary>
    private static readonly string[] AllowedLatinTokens = ["FileFerry", "Nextcloud", "GmbH", "IP", "DNS", "Wi-Fi", "LAN", "v1"];

    private static readonly HashSet<string> XmlFlavors =
        new(StringComparer.OrdinalIgnoreCase) { ".xaml", ".xml", ".resx", ".csproj", ".props" };

    /// <summary>
    /// Traces that make a comment unreadable without another document or another machine:
    /// a note section, a screen code, the bench a fact was observed on, a task identifier.
    /// </summary>
    private static readonly (string Why, string Pattern)[] CrossReferences =
    [
        ("cites a numbered section of a specification", @"(?:0[1-6]\s*)?§\s*\d"),
        ("cites a specification note by file name", @"docs[/\\]|[Aa]rchitecture\.md|[Dd]esign\.md|[Bb]acklog\.md"),
        ("names a screen by its code", @"\bS[0-8]\b"),
        ("records which device or stand a fact was observed on", @"\b(?:[F]FEmu|Pix[e]l|emulator)\b|\b\d{2}\.\d{2}\.(?:20)?\d{2}\b"),
        ("carries a task identifier", @"\b(?:[AB]-\d+|U-bug-\d+|U-\d+|Gate\s+[AB])\b"),
    ];

    /// <summary>
    /// Hosts a repository text may name: loopback (what a fake test server binds to), the names the
    /// protocol documents reserve for examples, the vocabularies markup declares as namespaces, the
    /// server product's own public pages, and the reserved local-name suffixes the app ships in its
    /// cleartext policy. A host outside this set is one particular machine somewhere.
    /// </summary>
    private static readonly string[] NamedHosts =
    [
        "localhost", "127.0.0.1", "example.com", "example.org", "example.net",
        "schemas.microsoft.com", "schemas.android.com", "www.apache.org", "sabredav.org", "owncloud.org",
        "nextcloud.com", "docs.nextcloud.com", "github.com",
        "home.arpa", "local", "lan", "intranet", "localdomain",
    ];

    /// <summary>
    /// Shapes that belong to one person's network or bench rather than to the product: a machine
    /// named under the reserved home-network suffix, a device or stand by its nickname. Each pattern
    /// is spelled so its own text never matches it — a bracket sits inside every forbidden word.
    /// </summary>
    private static readonly (string Why, string Pattern)[] EnvironmentTraces =
    [
        ("names a machine under a reserved home-network suffix", @"\b[A-Za-z][\w-]+(\.[\w-]+)+\.home\.arpa\b"),
        ("names the device or stand a fact was observed on", @"\bPix[e]l\b|\bteg[u]\b|\b[F]FEmu\b|\bemulator-\d+\b|\bngr[o]k\b"),
    ];

    private static readonly Regex UrlHost = new(@"https?://(?<host>[A-Za-z0-9.\-_]+)", RegexOptions.Compiled);

    private static readonly Regex DottedQuad = new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b(?!\.\d)", RegexOptions.Compiled);

    [Fact]
    public void Every_locale_defines_exactly_the_same_keys()
    {
        var locales = AppStringsFiles.AllLocales();
        Assert.True(locales.Count > 1, "no locale satellites were discovered — the resource naming convention changed");
        var reference = locales[string.Empty].Keys;

        var offenders = from locale in locales
                        where locale.Key.Length != 0
                        from problem in MissingOrExtra(reference, locale.Value.Keys, locale.Key)
                        select problem;

        AssertClean(offenders, "a locale must define the same keys as the neutral resources:");
    }

    [Fact]
    public void Every_key_keeps_its_placeholders_in_every_locale()
    {
        var offenders =
            from entry in FallbackComparisons(AppStringsFiles.AllLocales())
            where !AppStringsFiles.Placeholders(entry.Fallback).SequenceEqual(AppStringsFiles.Placeholders(entry.Value), StringComparer.Ordinal)
            select $"{entry.Culture}/{entry.Key}: expected {AppStringsFiles.Placeholders(entry.Fallback)}, found {AppStringsFiles.Placeholders(entry.Value)}";

        AssertClean(offenders, "format arguments must survive into every locale:");
    }

    [Fact]
    public void Localized_values_are_not_copies_of_the_fallback()
    {
        var offenders =
            from entry in FallbackComparisons(AppStringsFiles.AllLocales())
            where !LanguageFreeKeys.Contains(entry.Key)
            where string.IsNullOrWhiteSpace(entry.Value) || entry.Value == entry.Fallback
            select $"{entry.Culture}/{entry.Key} = \"{entry.Value}\"";

        AssertClean(offenders, "every locale carries its own wording (a value without a language belongs in LanguageFreeKeys):");
    }

    [Fact]
    public void Localized_values_use_the_script_of_their_locale()
    {
        var offenders =
            from locale in AppStringsFiles.AllLocales()
            where locale.Key.Length != 0
            from problem in ScriptProblems(locale.Key, locale.Value)
            select problem;

        AssertClean(offenders, "a locale declares the script of its values and writes every value in it:");
    }

    [Fact]
    public void No_foreign_script_word_slips_into_a_localized_value()
    {
        var offenders =
            from locale in AppStringsFiles.AllLocales()
            where locale.Key.Length != 0 && !LatinScriptLocales.Contains(locale.Key, StringComparer.OrdinalIgnoreCase)
            from entry in locale.Value
            from token in ForeignTokens(entry.Value)
            select $"{locale.Key}/{entry.Key}: \"{token}\" is a Latin word inside a value of another script — \"{entry.Value}\"";

        AssertClean(offenders, $"a locale translates its wording; a Latin word survives only as a brand or an acronym {AllowedLatinTokens}:");
    }

    [Fact]
    public void The_fallback_uses_plain_ascii_letters_only()
    {
        var offenders = AppStringsFiles.Entries(AppStringsFiles.Neutral)
            .Where(entry => entry.Value.Any(AppStringsFiles.IsNonAsciiLetter))
            .Select(entry => $"{entry.Key} = \"{entry.Value}\"");

        AssertClean(offenders, "the neutral resources are read by every locale without a satellite, so they stay in the base language:");
    }

    [Fact]
    public void Repository_text_uses_plain_ascii_letters_only()
    {
        var offenders = from file in SourceFiles()
                        where !IsLocaleResource(file) && !IsUnderTests(file)
                        from line in NonAsciiLetterLines(file)
                        select $"{AppStringsFiles.Relative(file)}:{line}";

        AssertClean(offenders, "text outside the locale resources is read by developers, not shown to a user:");
    }

    [Fact]
    public void No_comment_uses_letters_beyond_basic_latin()
    {
        var offenders = from file in SourceFiles("src").Concat(SourceFiles("tests"))
                        from line in CommentLines(file)
                        select $"{AppStringsFiles.Relative(file)}:{line}";

        AssertClean(offenders, "comments are written in the base language (a string literal is data):");
    }

    [Fact]
    public void No_comment_carries_a_cross_reference()
    {
        var offenders = from file in SourceFiles("src").Concat(SourceFiles("tests"))
                        from comment in CommentBodies(file)
                        from problem in CrossReferenceProblems(file, comment)
                        select problem;

        AssertClean(offenders, "a comment explains its reasoning in plain words, with nothing to look up elsewhere:");
    }

    [Fact]
    public void Every_key_is_reachable_from_code()
    {
        var accessors = AppStringsFiles.AccessorNames();
        var keys = AppStringsFiles.Entries(AppStringsFiles.Neutral).Keys;

        // Event/Error keys are resolved at render time by name (JournalPhrases/ErrorUi), not by accessor.
        var unreachable = keys
            .Where(key => !accessors.Contains(key)
                          && !(key.StartsWith("Event", StringComparison.Ordinal) || key.StartsWith("Error", StringComparison.Ordinal)))
            .Select(key => $"{key} (no accessor, not a render-time phrase key)");
        var undefined = accessors
            .Where(name => !keys.Contains(name))
            .Select(name => $"{name} (accessor without a resource entry)");

        AssertClean(unreachable.Concat(undefined), "AppStrings.cs and the resource files disagree:");
    }

    [Fact]
    public void No_repository_text_carries_a_pictograph()
    {
        var offenders = from file in SourceFiles()
                        from line in EmojiLines(file)
                        select $"{AppStringsFiles.Relative(file)}:{line}";

        AssertClean(offenders, "pictographs are neither written nor read as text, so no file of the repository holds one:");
    }

    [Fact]
    public void No_text_names_a_development_or_test_environment()
    {
        var offenders = from file in SourceFiles()
                        from problem in EnvironmentProblems(file)
                        select problem;

        AssertClean(offenders, "a repository that builds anywhere describes shapes and never names the network or the bench it was written on:");
    }

    /// <summary>
    /// Each locale value next to the fallback text of the same key. Keys the neutral file does not
    /// define are the business of the key-parity rule, so they are skipped here.
    /// </summary>
    private static IEnumerable<(string Culture, string Key, string Value, string Fallback)> FallbackComparisons(
        Dictionary<string, Dictionary<string, string>> locales)
    {
        foreach (var locale in locales)
        {
            if (locale.Key.Length == 0)
            {
                continue;
            }

            foreach (var entry in locale.Value)
            {
                if (locales[string.Empty].TryGetValue(entry.Key, out var fallback))
                {
                    yield return (locale.Key, entry.Key, entry.Value, fallback);
                }
            }
        }
    }

    /// <summary>
    /// A locale without a row is an offender rather than an unchecked gap: that is how a locale
    /// added to the resource folder gets the script of its values declared.
    /// </summary>
    private static IEnumerable<string> ScriptProblems(string culture, Dictionary<string, string> values)
    {
        if (!ExpectedScript.TryGetValue(culture, out var inScript))
        {
            yield return $"{culture}: no code-point range declared for this locale";
            yield break;
        }

        foreach (var entry in values)
        {
            if (!LanguageFreeKeys.Contains(entry.Key) && !entry.Value.Any(inScript))
            {
                yield return $"{culture}/{entry.Key} = \"{entry.Value}\"";
            }
        }
    }

    /// <summary>
    /// Latin words of a value that the allowlist of brands and acronyms does not cover — the shape an
    /// untranslated term takes inside a value of another script.
    /// </summary>
    private static IEnumerable<string> ForeignTokens(string value) =>
        Regex.Matches(value, "[A-Za-z][A-Za-z0-9+.-]*")
            .Select(match => match.Value)
            .Where(word => !AllowedLatinTokens.Contains(word.Trim('.', '-', '/'), StringComparer.Ordinal));

    /// <summary>
    /// Lines of a file that name somebody's environment: a URL host outside the documented and
    /// shipped names, an address in private-use space, a home-network fully-qualified name, a
    /// device or stand nickname.
    /// </summary>
    private static IEnumerable<string> EnvironmentProblems(string file)
    {
        var relative = AppStringsFiles.Relative(file);
        var lines = File.ReadAllLines(file);

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match host in UrlHost.Matches(lines[i]))
            {
                var name = host.Groups["host"].Value.TrimEnd('.');
                if (DottedQuad.IsMatch(name))
                {
                    // an address in a URL is judged as an address: examples and loopback are fine,
                    // private space is one particular network
                    if (IsPrivateAddress(name))
                    {
                        yield return $"{relative}:{i + 1}: \"{name}\" is an address a real network assigned, not a documented example one";
                    }
                }
                else if (!IsNamedHost(name))
                {
                    yield return $"{relative}:{i + 1}: \"{name}\" is the address of one particular machine, not a reserved or shipped name";
                }
            }

            foreach (Match quad in DottedQuad.Matches(lines[i]))
            {
                if (IsPrivateAddress(quad.Value))
                {
                    yield return $"{relative}:{i + 1}: \"{quad.Value}\" is an address a real network assigned, not a documented example one";
                }
            }

            foreach (var (why, pattern) in EnvironmentTraces)
            {
                var trace = Regex.Match(lines[i], pattern);
                if (trace.Success)
                {
                    yield return $"{relative}:{i + 1}: \"{trace.Value}\" {why}";
                }
            }
        }
    }

    /// <summary>
    /// Names reserved for examples extend to any leading label (<c>cloud.example</c>,
    /// <c>srv.invalid</c>); the listed names stand complete.
    /// </summary>
    private static bool IsNamedHost(string host) =>
        NamedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
        || host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Private-use space — where a real network numbers its machines. Digit groups beyond a byte
    /// are version numbers, not addresses.
    /// </summary>
    private static bool IsPrivateAddress(string quad)
    {
        var octets = quad.Split('.').Select(int.Parse).ToArray();
        return octets.All(octet => octet <= 255)
               && (octets[0] == 10
                   || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
                   || (octets[0] == 192 && octets[1] == 168));
    }

    private static IEnumerable<string> MissingOrExtra(IEnumerable<string> reference, IEnumerable<string> localeKeys, string culture)
    {
        var expected = reference.ToHashSet(StringComparer.Ordinal);
        var actual = localeKeys.ToHashSet(StringComparer.Ordinal);

        return expected.Except(actual, StringComparer.Ordinal).Select(key => $"{culture}: missing {key}")
            .Concat(actual.Except(expected, StringComparer.Ordinal).Select(key => $"{culture}: unknown {key}"));
    }

    /// <summary>Locale satellites are the only place where other scripts are legitimate.</summary>
    private static bool IsLocaleResource(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("AppStrings.", StringComparison.Ordinal) && !name.Equals("AppStrings.resx", StringComparison.Ordinal);
    }

    /// <summary>Text files of the repository (or of one tree), build output excluded — it holds generated copies.</summary>
    private static IEnumerable<string> SourceFiles(string? tree = null) =>
        Directory.EnumerateFiles(tree is null ? AppStringsFiles.Root : Path.Combine(AppStringsFiles.Root, tree), "*.*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => Extensions.Contains(Path.GetExtension(path)));

    /// <summary>Test sources may hold other alphabets inside a string literal — checked separately as comments.</summary>
    private static bool IsUnderTests(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}");

    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".xml", ".resx", ".csproj", ".props", ".editorconfig", ".md" };

    private static IEnumerable<int> NonAsciiLetterLines(string file) =>
        File.ReadAllLines(file)
            .Select((line, index) => (line, index))
            .Where(pair => pair.line.Any(AppStringsFiles.IsNonAsciiLetter))
            .Select(pair => pair.index + 1);

    /// <summary>Lines of any file — code, resources, notes — that hold a pictograph.</summary>
    private static IEnumerable<int> EmojiLines(string file) =>
        File.ReadAllLines(file)
            .Select((line, index) => (line, index))
            .Where(pair => AppStringsFiles.HasEmoji(pair.line))
            .Select(pair => pair.index + 1);

    /// <summary>
    /// Lines whose letters beyond Basic Latin sit in a comment (a whole-line comment, a trailing
    /// <c>//</c> outside a string literal, or an XML comment). String literals may hold other
    /// alphabets on purpose: they are fixtures of unicode paths
    /// (<c>RelativePath</c>/SQLite round-trips).
    /// </summary>
    private static IEnumerable<int> CommentLines(string file)
    {
        var lines = File.ReadAllLines(file);
        var isXml = XmlFlavors.Contains(Path.GetExtension(file));
        var offenders = new List<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var comment = isXml ? XmlComment(lines[i]) : CsComment(lines[i]);
            if (comment.Any(AppStringsFiles.IsNonAsciiLetter))
            {
                offenders.Add(i + 1);
            }
        }

        return offenders;
    }

    /// <summary>Every comment body of a file with its line — the text a convention on comments binds.</summary>
    private static IEnumerable<(int Line, string Text)> CommentBodies(string file)
    {
        var lines = File.ReadAllLines(file);
        var isXml = XmlFlavors.Contains(Path.GetExtension(file));

        for (var i = 0; i < lines.Length; i++)
        {
            var comment = isXml ? XmlComment(lines[i]) : CsComment(lines[i]);
            if (comment.Length != 0)
            {
                yield return (i + 1, comment);
            }
        }
    }

    private static IEnumerable<string> CrossReferenceProblems(string file, (int Line, string Text) comment) =>
        from rule in CrossReferences
        where Regex.IsMatch(comment.Text, rule.Pattern)
        select $"{AppStringsFiles.Relative(file)}:{comment.Line} {rule.Why}: \"{comment.Text.Trim()}\"";

    private static string XmlComment(string line)
    {
        var start = line.IndexOf("<!--", StringComparison.Ordinal);
        var end = line.IndexOf("-->", StringComparison.Ordinal);
        return start >= 0 && end > start ? line[(start + 4)..end] : string.Empty;
    }

    /// <summary>Single-line heuristic: block comments are matched by their leading <c>/*</c> or <c>*</c>.</summary>
    private static string CsComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal))
        {
            return line;
        }

        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[i..];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A rule fails with the list of offenders — a bare "sequence is empty" says nothing.
    /// </summary>
    private static void AssertClean(IEnumerable<string> offenders, string rule)
    {
        var list = offenders.ToList();
        Assert.True(list.Count == 0, $"{rule}{Environment.NewLine}{string.Join(Environment.NewLine, list)}");
    }
}
