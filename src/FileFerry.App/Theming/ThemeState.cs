namespace FileFerry.App.Theming;

/// <summary>
/// A stamp of the current palette resolution. Every re-map the theme service performs bumps the
/// <see cref="Version"/>, and views that resolve token names to concrete colors bind through it:
/// the source token string never changes, yet the color behind it does, and the binding needs a
/// second, changing source to re-evaluate. A process-wide singleton — the stamp is the theme's,
/// not any page's.
/// </summary>
public sealed class ThemeState : Mvvm.ObservableObject
{
    /// <summary>
    /// The one stamp instance the whole app binds against.
    /// </summary>
    public static ThemeState Instance { get; } = new();

    private int _version;

    private ThemeState()
    {
    }

    /// <summary>
    /// Count of palette re-maps since the process started.
    /// </summary>
    public int Version => _version;

    /// <summary>
    /// Signals one completed re-map; called only by the theme service after resources were rewritten.
    /// </summary>
    internal void Bump() => Set(ref _version, _version + 1, nameof(Version));
}
