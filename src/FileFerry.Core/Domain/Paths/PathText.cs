using System.Text;

namespace FileFerry.Core.Domain.Paths;

/// <summary>
/// Utility normalizer of textual paths for the value objects (<c>RelativePath</c>, <c>RemotePath</c>).
/// Pipeline: (opt.) percent-decode → Unicode NFC → split on <c>'/'</c> → drop empty and <c>"."</c>
/// segments → reject <c>"..</c>" (escaping the root) → join with single <c>'/'</c>.
/// Implements "parse, don't validate": the output is always a canonical string or a failure.
/// </summary>
internal static class PathText
{
    /// <summary>
    /// Canonical segment separator of the normalized model.
    /// </summary>
    internal const char Separator = '/';

    /// <summary>
    /// Whether forbidden characters occur — control chars (NUL, \r, \n etc.) at any position.
    /// </summary>
    private static bool HasInvalidChars(string text) => text.Any(static c => char.IsControl(c));

    /// <summary>
    /// Normalizes <paramref name="input"/> into a canonical relative path without a leading/trailing <c>'/'</c>.
    /// </summary>
    /// <param name="input">Raw path; <see langword="null"/> — always a failure.</param>
    /// <param name="percentDecode"><see langword="true"/> — percent-escapes are decoded first (for remote hrefs).</param>
    /// <param name="value">Canonical result; <see cref="string.Empty"/> — the root.</param>
    /// <returns><see langword="false"/> when the path is impossible (null, control chars, <c>..</c> escape).</returns>
    internal static bool TryNormalize(string? input, bool percentDecode, out string value)
    {
        value = string.Empty;
        if (input is null)
        {
            return false;
        }

        if (percentDecode)
        {
            // UnescapeDataString does not throw on broken escape sequences — it leaves them as literals.
            input = Uri.UnescapeDataString(input);
        }

        // NFC folds NFD equivalents ("e"+U+0301 → U+00E9) so path comparisons stay stable.
        input = input.Trim().Normalize(NormalizationForm.FormC);
        if (input.Length == 0)
        {
            return true; // empty string or separators only — that is the root
        }

        if (HasInvalidChars(input))
        {
            return false;
        }

        StringBuilder? buffer = null;
        foreach (var segment in input.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue; // "a/./b" ≡ "a/b"
            }

            if (segment == "..")
            {
                return false; // a path inside a pair never escapes the root
            }

            buffer ??= new StringBuilder(input.Length);
            if (buffer.Length > 0)
            {
                buffer.Append(Separator);
            }

            buffer.Append(segment);
        }

        value = buffer?.ToString() ?? string.Empty;
        return true;
    }
}
