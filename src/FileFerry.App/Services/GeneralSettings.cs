using FileFerry.Core.Domain;

namespace FileFerry.App.Services;

/// <summary>
/// Global user settings that belong to no pair (settings screen): the "default" values for new pairs.
/// Stored in <c>Preferences</c> (not a secret).
/// </summary>
public interface IGeneralSettings
{
    /// <summary>
    /// Interval for new pairs (wizard step 3 starts from it).
    /// </summary>
    SyncSchedule DefaultSchedule { get; }

    /// <summary>
    /// Whether new pairs are born with the "Wi-Fi/LAN only" constraint.
    /// </summary>
    bool DefaultWifiOnly { get; }

    /// <summary>
    /// Save the default interval.
    /// </summary>
    Task SetDefaultScheduleAsync(SyncSchedule value, CancellationToken ct);

    /// <summary>
    /// Save the default "Wi-Fi/LAN only" constraint.
    /// </summary>
    Task SetDefaultWifiOnlyAsync(bool value, CancellationToken ct);
}

/// <summary>
/// Adapter of <see cref="IGeneralSettings"/> over MAUI <c>Preferences</c>: simple scalars where a
/// SQLite table would be overkill (in the spirit Options: defaults are edited from the UI).
/// </summary>
public sealed class PreferencesGeneralSettings : IGeneralSettings
{
    private const string ScheduleKey = "settings.default_schedule";
    private const string WifiOnlyKey = "settings.default_wifi_only";

    /// <inheritdoc />
    public SyncSchedule DefaultSchedule =>
        (SyncSchedule)Preferences.Default.Get(ScheduleKey, (int)SyncSchedule.Every15Minutes);

    /// <inheritdoc />
    public bool DefaultWifiOnly => Preferences.Default.Get(WifiOnlyKey, true);

    /// <inheritdoc />
    public Task SetDefaultScheduleAsync(SyncSchedule value, CancellationToken ct)
    {
        Preferences.Default.Set(ScheduleKey, (int)value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetDefaultWifiOnlyAsync(bool value, CancellationToken ct)
    {
        Preferences.Default.Set(WifiOnlyKey, value);
        return Task.CompletedTask;
    }
}
