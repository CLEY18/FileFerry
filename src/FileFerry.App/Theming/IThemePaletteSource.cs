namespace FileFerry.App.Theming;

/// <summary>
/// "Dynamic" palette source port (/): declared by its consumer
/// (<see cref="ThemeService"/>), the adapter lives in Platforms/Android — Core knows
/// nothing about theming.
/// </summary>
public interface IThemePaletteSource
{
    /// <summary>
    /// Whether the system palette is available on this device (Android 12+).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Token → value for the current light/dark. Returns only the keys the system
    /// overrides; absent keys stay on the base NC palette (<see cref="ThemeService"/> does the merge).
    /// </summary>
    IReadOnlyDictionary<string, Color> GetPalette(AppTheme theme);
}
