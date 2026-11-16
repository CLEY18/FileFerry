namespace FileFerry.App.Services;

/// <summary>
/// Battery-optimization state with respect to the app (settings screen): without the ignore exemption
/// WorkManager may delay background runs substantially. The port is declared by the consumer;
/// the adapter is Android.
/// </summary>
public interface IBatteryOptimization
{
    /// <summary>
    /// Whether the app is already excluded from optimization.
    /// </summary>
    bool IsIgnored { get; }

    /// <summary>
    /// Open the system ignore request (a dialog or the settings screen, depending on ROM policy);
    /// returns once the screen is handed over to the system.
    /// </summary>
    Task OpenRequestAsync(CancellationToken ct);
}
