using FileFerry.App.Theming;

namespace FileFerry.App;

/// <summary>
/// Android bridge of the dynamic palette: system Material You colors (Android 12+)
/// via <c>android.R.color.system_*</c>. Below API 31 <see cref="IsAvailable"/> is false and
/// <see cref="ThemeService"/> keeps the static NC palette.
/// </summary>
public sealed class AndroidThemePalette : IThemePaletteSource
{
    /// <summary>
    /// FileFerry token → system name (light, dark). Status colors are not mapped: the system
    /// does not export them, so the semantic values remain in place.
    /// </summary>
    private static readonly (string Token, string Light, string Dark)[] Map =
    [
        ("Primary", "system_accent1_500", "system_accent1_300"),
        ("PrimaryContainer", "system_accent1_100", "system_accent1_800"),
        ("PrimaryText", "system_accent1_600", "system_accent1_200"),
        ("Background", "system_neutral1_10", "system_neutral1_900"),
        ("Surface", "system_neutral1_20", "system_neutral1_800"),
        ("SurfaceVariant", "system_neutral2_90", "system_neutral2_800"),
        ("TextPrimary", "system_neutral1_900", "system_neutral1_10"),
        ("TextSecondary", "system_neutral2_600", "system_neutral2_300"),
        ("Border", "system_neutral2_200", "system_neutral2_700"),
    ];

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsAndroidVersionAtLeast(31);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, Color> GetPalette(AppTheme theme)
    {
        var result = new Dictionary<string, Color>();
        var resources = Android.App.Application.Context.Resources;
        if (!IsAvailable || resources is null)
        {
            return result;
        }

        var dark = theme == AppTheme.Dark;
        foreach (var (token, light, darkName) in Map)
        {
            var id = resources.GetIdentifier(dark ? darkName : light, "color", "android");
            if (id == 0)
            {
                continue; // guard: a non-standard ROM without the system resource — the token stays at baseline
            }

            var color = resources.GetColor(id, null);
            result[token] = Color.FromRgb(color.R, color.G, color.B);
        }

        return result;
    }
}
