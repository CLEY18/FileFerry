using System.Collections.ObjectModel;
using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.App.Theming;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the settings screen: theme segment, defaults for new pairs, battery-optimization
/// status, account with logout, navigation to log/about. No policy here —
/// it lives in the services themselves.
/// </summary>
public sealed class SettingsViewModel : Mvvm.ObservableObject
{
    private readonly IThemeService _theme;
    private readonly IGeneralSettings _settings;
    private readonly IBatteryOptimization _battery;
    private readonly IAccountRepository _accounts;
    private readonly IFolderPairRepository _pairs;
    private readonly ISecretStore _secrets;
    private readonly INavigationService _navigation;
    private readonly IAppDialogs _dialogs;
    private readonly ISyncScheduler _scheduler;

    private string _defaultIntervalText = string.Empty;
    private bool _wifiOnlyDefault;
    private string _batteryStatusText = string.Empty;
    private string _accountLine = AppStrings.AccountChipEmpty;

    public SettingsViewModel(
        IThemeService theme,
        IGeneralSettings settings,
        IBatteryOptimization battery,
        IAccountRepository accounts,
        IFolderPairRepository pairs,
        ISecretStore secrets,
        INavigationService navigation,
        IAppDialogs dialogs,
        ISyncScheduler scheduler)
    {
        _theme = theme;
        _settings = settings;
        _battery = battery;
        _accounts = accounts;
        _pairs = pairs;
        _secrets = secrets;
        _navigation = navigation;
        _dialogs = dialogs;
        _scheduler = scheduler;

        Themes =
        [
            new ThemeChip(ThemeMode.Nextcloud, AppStrings.ThemeNextcloudLabel),
            new ThemeChip(ThemeMode.Dynamic, AppStrings.ThemeDynamicLabel),
        ];
        foreach (var chip in Themes)
        {
            var mode = chip.Value;
            chip.SelectCommand = new Mvvm.AsyncRelayCommand(async (_, ct) =>
            {
                await _theme.SetModeAsync(mode, ct);
                ApplyThemeSelection();
            });
        }

        ChangeDefaultIntervalCommand = new Mvvm.AsyncRelayCommand(ChangeDefaultIntervalAsync);
        BatteryCommand = new Mvvm.AsyncRelayCommand(async (_, ct) => await _battery.OpenRequestAsync(ct));
        LogoutCommand = new Mvvm.AsyncRelayCommand(LogoutAsync);
        OpenAboutCommand = new Mvvm.AsyncRelayCommand((_, ct) => _navigation.GoAsync(AppShell.AboutRoute, ct));
        PrivacyCommand = new Mvvm.AsyncRelayCommand(
            (_, ct) => _dialogs.ShowAlertAsync(
                AppStrings.PendingSectionTitle,
                string.Format(AppStrings.PendingSectionBody, AppStrings.SettingsPrivacyTitle),
                AppStrings.HelpOk,
                ct));
    }

    /// <summary>
    /// Palette segment (Nextcloud / Dynamic).
    /// </summary>
    public ObservableCollection<ThemeChip> Themes { get; }

    /// <summary>
    /// Default interval label for new pairs.
    /// </summary>
    public string DefaultIntervalText
    {
        get => _defaultIntervalText;
        private set => Set(ref _defaultIntervalText, value);
    }

    /// <summary>
    /// "Wi-Fi/LAN only" constraint for new pairs (two-way switch).
    /// </summary>
    public bool WifiOnlyDefault
    {
        get => _wifiOnlyDefault;
        set
        {
            if (Set(ref _wifiOnlyDefault, value))
            {
                _ = _settings.SetDefaultWifiOnlyAsync(value, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Battery status: reliable background vs. possible delays (fact + consequence).
    /// </summary>
    public string BatteryStatusText
    {
        get => _batteryStatusText;
        private set => Set(ref _batteryStatusText, value);
    }

    /// <summary>
    /// Account line "user@host" (v1 — a single account).
    /// </summary>
    public string AccountLine
    {
        get => _accountLine;
        private set => Set(ref _accountLine, value);
    }

    /// <summary>
    /// Sheet for choosing the default interval (same vocabulary as the wizard).
    /// </summary>
    public Mvvm.AsyncRelayCommand ChangeDefaultIntervalCommand { get; }

    /// <summary>
    /// System request to ignore battery optimization.
    /// </summary>
    public Mvvm.AsyncRelayCommand BatteryCommand { get; }

    /// <summary>
    /// Logout: secret + account removed, pairs cascade-deleted, the shell is rebuilt.
    /// </summary>
    public Mvvm.AsyncRelayCommand LogoutCommand { get; }

    /// <summary>
    /// Navigate to the about screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand OpenAboutCommand { get; }

    /// <summary>
    /// Privacy policy — the link ships with the release (no URL in the docs yet).
    /// </summary>
    public Mvvm.AsyncRelayCommand PrivacyCommand { get; }

    /// <summary>
    /// Reload all values (OnAppearing: the user may have changed system settings outside the screen).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        ApplyThemeSelection();
        DefaultIntervalText = ScheduleLabel(_settings.DefaultSchedule);
        _wifiOnlyDefault = _settings.DefaultWifiOnly;
        Raise(nameof(WifiOnlyDefault));
        BatteryStatusText = _battery.IsIgnored ? AppStrings.BatteryIgnored : AppStrings.BatteryNotIgnored;

        var accounts = await _accounts.ListAsync(ct);
        await Application.Current!.Dispatcher.DispatchAsync(() =>
            AccountLine = accounts.Count > 0 ? $"{accounts[0].UserName}@{new Uri(accounts[0].BaseUrl).Host}" : AppStrings.AccountChipEmpty);
    }

    /// <summary>
    /// Syncs the segment selection with the real mode of <see cref="IThemeService"/>.
    /// </summary>
    private void ApplyThemeSelection()
    {
        foreach (var chip in Themes)
        {
            chip.IsSelected = chip.Value == _theme.Mode;
        }
    }

    /// <summary>
    /// Sheet of the four known intervals → store the default (does not affect already-created pairs).
    /// </summary>
    private async Task ChangeDefaultIntervalAsync(CancellationToken ct)
    {
        var known = ChoiceOption.KnownSchedules();
        var choice = await _dialogs.ShowActionAsync(
            AppStrings.SettingsDefaultIntervalTitle, AppStrings.DialogCancel, known.Select(k => k.Label).ToList(), ct);
        if (choice >= 0)
        {
            await _settings.SetDefaultScheduleAsync(known[choice].Value, ct);
            await Application.Current!.Dispatcher.DispatchAsync(() => DefaultIntervalText = known[choice].Label);
        }
    }

    /// <summary>
    /// Logout: remove the token from the Keystore → delete the account (the FK cascade takes the
    /// pairs with it — we warn honestly), then rebuild the shell: the startup gate itself
    /// lands the logged-out user on the welcome screen.
    /// </summary>
    private async Task LogoutAsync(CancellationToken ct)
    {
        var accounts = await _accounts.ListAsync(ct);
        if (accounts.Count == 0)
        {
            return;
        }

        var account = accounts[0];
        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.LogoutTitle, AppStrings.LogoutBody, AppStrings.AccountLogout, AppStrings.DialogCancel, ct);
        if (!confirmed)
        {
            return;
        }

        // cancel all pairs' background jobs while the SQLite cascade has not taken them yet
        foreach (var pair in await _pairs.ListAsync(ct))
        {
            await _scheduler.CancelAsync(pair.Id, ct);
        }

        await _secrets.RemoveAsync(account.TokenRef, ct);
        await _accounts.DeleteAsync(account.Id, ct);
        await Application.Current!.Dispatcher.DispatchAsync(() =>
            Application.Current.Windows[0].Page = new AppShell());
    }

    /// <summary>
    /// Schedule label — the chip vocabulary shared with the wizard chips.
    /// </summary>
    private static string ScheduleLabel(SyncSchedule schedule) =>
        ChoiceOption.KnownSchedules().First(k => k.Value == schedule).Label;
}
