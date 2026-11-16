namespace FileFerry.App.Theming;

/// <summary>
/// Palette mode; the default is <see cref="Nextcloud"/>.
/// </summary>
public enum ThemeMode
{
    /// <summary>
    /// Static NC palette: the / tokens unchanged.
    /// </summary>
    Nextcloud,

    /// <summary>
    /// Material You from the brand seed color; below Android 12 falls back to <see cref="Nextcloud"/>.
    /// </summary>
    Dynamic,
}
