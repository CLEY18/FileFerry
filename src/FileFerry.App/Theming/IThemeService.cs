namespace FileFerry.App.Theming;

/// <summary>
/// Single theme-switching point: the UI consumes tokens; the mode only
/// re-maps their values.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Current mode (persisted across launches).
    /// </summary>
    ThemeMode Mode { get; }

    /// <summary>
    /// Apply the stored mode to application resources; called once at window start.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Change the mode, persist it and re-map tokens immediately (DynamicResource consumers update themselves).
    /// </summary>
    Task SetModeAsync(ThemeMode mode, CancellationToken ct = default);
}
