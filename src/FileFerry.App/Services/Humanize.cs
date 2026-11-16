using System.Globalization;
using FileFerry.App.Resources;

namespace FileFerry.App.Services;

/// <summary>
/// Humanizing time and counters for the UI (relative time in lists). Formatting only, no
/// policies; the wording itself comes exclusively from the localized string resources.
/// </summary>
public static class Humanize
{
    /// <summary>
    /// Languages whose counters take a "few" form besides "one"/"many"; every other language,
    /// including the base one, uses the two forms available in its resources.
    /// </summary>
    private static readonly HashSet<string> ThreeFormLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "uk", "be", "ru", "pl", "cs", "sk", "hr", "sr", "bs", "sl", "bg", "mk" };

    /// <summary>
    /// Plural-form picker for the interface language, taken from <c>CultureInfo.CurrentUICulture</c>.
    /// Base rule — <paramref name="one"/> at 1 and <paramref name="many"/> everywhere else
    /// ("1 conflict / 2 conflicts"); a <see cref="ThreeFormLanguages"/> language inserts
    /// <paramref name="few"/> at 2–4.
    /// </summary>
    /// <param name="one">Form for 1 (and 21, 31 … in three-form languages).</param>
    /// <param name="few">Form for 2–4 except 12–14; the same word as <paramref name="many"/> where the language has no "few".</param>
    /// <param name="many">Form for the rest.</param>
    public static string Count(int n, string one, string few, string many)
    {
        if (!ThreeFormLanguages.Contains(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName))
        {
            return n == 1 ? one : many;
        }

        return n % 100 is >= 11 and <= 14
            ? many
            : n % 10 is 1 ? one : n % 10 is >= 2 and <= 4 ? few : many;
    }

    /// <summary>
    /// "just now / N min ago / N h ago / N d ago" for a past moment.
    /// </summary>
    /// <param name="now">The present moment (from <c>IClock</c> — testable).</param>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        return age switch
        {
            { TotalSeconds: < 45 } => AppStrings.RtNow,
            { TotalMinutes: < 90 } => string.Format(AppStrings.RtMinAgo, Math.Max(1, (int)age.TotalMinutes)),
            { TotalHours: < 24 } => string.Format(AppStrings.RtHourAgo, (int)age.TotalHours),
            _ => string.Format(AppStrings.RtDayAgo, (int)age.TotalDays),
        };
    }

    /// <summary>
    /// Run duration: "N s", "N min M s" or "N h M min".
    /// </summary>
    public static string Duration(TimeSpan duration) =>
        duration switch
        {
            { TotalMinutes: < 1 } => string.Format(AppStrings.DurSec, Math.Max(1, (int)duration.TotalSeconds)),
            { TotalHours: < 1 } => string.Format(AppStrings.DurMinSec, (int)duration.TotalMinutes, duration.Seconds),
            _ => string.Format(AppStrings.DurHourMin, (int)duration.TotalHours, duration.Minutes),
        };

    /// <summary>
    /// Short TTL remainder: "N d", "N h" or "N min" (minimum 1 min).
    /// </summary>
    /// <param name="remaining">What is left until the TTL expires.</param>
    public static string ShortLeft(TimeSpan remaining) =>
        remaining switch
        {
            { TotalDays: >= 1 } => string.Format(AppStrings.ShortDays, (int)remaining.TotalDays),
            { TotalHours: >= 1 } => string.Format(AppStrings.ShortHours, (int)remaining.TotalHours),
            _ => string.Format(AppStrings.ShortMinutes, Math.Max(1, (int)remaining.TotalMinutes)),
        };
}
