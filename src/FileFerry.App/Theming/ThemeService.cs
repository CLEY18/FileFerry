using Microsoft.Maui.Storage;

namespace FileFerry.App.Theming;

/// <summary>
/// <see cref="IThemeService"/> implementation: persists the mode in Preferences and
/// resolves "token → value" — light/dark palette (key + Dark suffix in Colors.xaml)
/// plus an overlaid Material You palette (Dynamic). This is where 's
/// "the mode only re-maps values" is implemented; returning to
/// <see cref="ThemeMode.Nextcloud"/> restores the exact baseline.
/// </summary>
public sealed class ThemeService : IThemeService
{
    /// <summary>
    /// Stored-mode key (not a secret — plain Preferences).
    /// </summary>
    private const string ModeKey = "theme_mode";

    /// <summary>
    /// Tokens resolved by the service = Colors.xaml token names.
    /// </summary>
    private static readonly string[] TokenKeys =
    [
        "Primary", "PrimaryHover", "PrimaryText", "OnPrimary", "PrimaryContainer",
        "Background", "Surface", "SurfaceVariant", "TextPrimary", "TextSecondary",
        "TextDisabled", "Border", "Selected", "StatusSuccess", "StatusError",
        "StatusWarning", "StatusInfo", "StatusWarningContainer", "StatusErrorContainer",
    ];

    private readonly IThemePaletteSource palette;
    private readonly Dictionary<string, object> lightBaseline = [];
    private bool initialized;

    /// <inheritdoc />
    public event EventHandler? Applied;

    /// <inheritdoc />
    public ThemeMode Mode { get; private set; } = ThemeMode.Nextcloud;

    public ThemeService(IThemePaletteSource palette) => this.palette = palette;

    /// <summary>
    /// Read the stored mode (used by <see cref="Initialize"/> at window start).
    /// </summary>
    public static ThemeMode LoadStoredMode() =>
        Enum.TryParse<ThemeMode>(Preferences.Default.Get(ModeKey, nameof(ThemeMode.Nextcloud)), out var mode)
            ? mode
            : ThemeMode.Nextcloud;

    /// <inheritdoc />
    public void Initialize()
    {
        var app = Application.Current;
        if (app is null || initialized)
        {
            return;
        }

        initialized = true;
        foreach (var key in TokenKeys)
        {
            if (app.Resources.TryGetValue(key, out var value) && value is not null)
            {
                lightBaseline[key] = value;
            }
        }

        app.RequestedThemeChanged += (_, _) => Apply();
        Mode = LoadStoredMode();
        Apply();
    }

    /// <inheritdoc />
    public Task SetModeAsync(ThemeMode mode, CancellationToken ct = default)
    {
        Preferences.Default.Set(ModeKey, mode.ToString());
        Mode = mode;
        Apply();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-map tokens: priority is Dynamic overlay &gt; Dark variant &gt; light baseline.
    /// lacks dark values for a few tokens — there the light value serves as fallback.
    /// </summary>
    private void Apply()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var overlay = Mode == ThemeMode.Dynamic && palette.IsAvailable
            ? palette.GetPalette(app.RequestedTheme)
            : null;
        var dark = app.RequestedTheme == AppTheme.Dark;

        foreach (var (key, light) in lightBaseline)
        {
            var value = dark && app.Resources.TryGetValue(key + "Dark", out var darkValue) && darkValue is not null
                ? darkValue
                : light;
            app.Resources[key] = overlay is not null && overlay.TryGetValue(key, out var color) ? color : value;
        }

        Applied?.Invoke(this, EventArgs.Empty);
        // views resolving token names to concrete colors re-read them through this stamp
        ThemeState.Instance.Bump();
    }
}
