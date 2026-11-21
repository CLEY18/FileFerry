using System.Collections.ObjectModel;
using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the add-pair wizard: three steps on one route — ① remote browser (uses only
/// <see cref="IRemoteBrowser"/>, the ISP from Core), ② SAF pick of the local folder, ③ schedule and
/// saving <see cref="FolderPair"/>. Policy (what may be created at all) is not here —
/// this only collects configuration.
/// </summary>
public sealed class PairWizardViewModel : Mvvm.ObservableObject
{
    private readonly IAccountRepository _accounts;
    private readonly RemoteStoreFactory _storeFactory;
    private readonly ILocalFolderPicker _folderPicker;
    private readonly IFolderPairRepository _pairs;
    private readonly INavigationService _navigation;
    private readonly IAppDialogs _dialogs;
    private readonly ISyncScheduler _scheduler;
    private readonly INotificationPermission _notifications;

    private Account? _account;
    private IRemoteBrowser? _browser;
    private RemotePath _current = RemotePath.Root;
    private RemotePath? _chosenRemote;
    private PickedFolder? _local;
    private int _step = 1;
    private bool _isBusy;
    private bool _wifiOnly = true;
    private string _breadcrumb = AppStrings.WizardRootCrumb;
    private string? _errorText;

    public PairWizardViewModel(
        IAccountRepository accounts,
        RemoteStoreFactory storeFactory,
        ILocalFolderPicker folderPicker,
        IFolderPairRepository pairs,
        INavigationService navigation,
        IAppDialogs dialogs,
        IGeneralSettings defaults,
        ISyncScheduler scheduler,
        INotificationPermission notifications)
    {
        _accounts = accounts;
        _storeFactory = storeFactory;
        _folderPicker = folderPicker;
        _pairs = pairs;
        _navigation = navigation;
        _dialogs = dialogs;
        _scheduler = scheduler;
        _notifications = notifications;
        _wifiOnly = defaults.DefaultWifiOnly;

        UpCommand = new Mvvm.AsyncRelayCommand(_ => GoUpAsync(), () => !IsBusy);
        PickLocalCommand = new Mvvm.AsyncRelayCommand(PickLocalAsync, () => !IsBusy);
        PrimaryCommand = new Mvvm.AsyncRelayCommand(RunPrimaryAsync, () => !IsBusy);

        foreach (var (value, label) in ChoiceOption.KnownSchedules())
        {
            var chip = new ChoiceOption(value, label) { IsSelected = value == defaults.DefaultSchedule };
            chip.SelectCommand = new Mvvm.AsyncRelayCommand(_ =>
            {
                foreach (var other in Schedules)
                {
                    other.IsSelected = ReferenceEquals(other, chip); // a segment — exactly one selected
                }

                return Task.CompletedTask;
            });
            Schedules.Add(chip);
        }
    }

    /// <summary>
    /// Folders of the current level (directories only — a pair is a folder-root).
    /// </summary>
    public ObservableCollection<RemoteBrowserRow> Folders { get; } = [];

    /// <summary>
    /// Schedule chips (step 3); exactly one selected.
    /// </summary>
    public ObservableCollection<ChoiceOption> Schedules { get; } = [];

    /// <summary>
    /// Current remote path (step 1).
    /// </summary>
    public RemotePath Current => _current;

    /// <summary>
    /// "Breadcrumbs" of the current folder (13, add-pair wizard).
    /// </summary>
    public string Breadcrumb
    {
        get => _breadcrumb;
        private set => Set(ref _breadcrumb, value);
    }

    /// <summary>
    /// Chosen remote root (after step 1).
    /// </summary>
    public RemotePath? ChosenRemote => _chosenRemote;

    /// <summary>
    /// Chosen local folder; null — not picked yet (step 2).
    /// </summary>
    public PickedFolder? Local
    {
        get => _local;
        private set
        {
            if (Set(ref _local, value))
            {
                Raise(nameof(HasLocal));
                RaisePrimary();
            }
        }
    }

    /// <summary>
    /// Current step (1..3) — drives the Visible sections and the indicator.
    /// </summary>
    public int Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                RaiseStep();
                RaisePrimary();
            }
        }
    }

    /// <summary>
    /// A network operation / picker is running — controls are blocked.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RaisePrimary();
                // All canExecute="!IsBusy" must be re-raised, otherwise buttons mounted during
                // loading stay disabled forever (seen with PickLocal on step 2).
                UpCommand.RaiseCanExecuteChanged();
                PickLocalCommand.RaiseCanExecuteChanged();
                PrimaryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// "Wi-Fi/LAN only" toggle (step 3; the background constraints).
    /// </summary>
    public bool WifiOnly
    {
        get => _wifiOnly;
        set => Set(ref _wifiOnly, value);
    }

    /// <summary>
    /// Inline error (PROPFIND/network failure); null — all clear.
    /// </summary>
    public string? ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    /// <summary>
    /// "Up" from step 1 (one level above the current folder).
    /// </summary>
    public Mvvm.AsyncRelayCommand UpCommand { get; }

    /// <summary>
    /// Step 2: the system SAF folder picker.
    /// </summary>
    public Mvvm.AsyncRelayCommand PickLocalCommand { get; }

    /// <summary>
    /// The step's primary action: "Choose this folder" → "Next" → "Create pair".
    /// </summary>
    public Mvvm.AsyncRelayCommand PrimaryCommand { get; }

    /// <summary>
    /// Show "Up" in the breadcrumbs when not at the root.
    /// </summary>
    public bool CanGoUp => !Current.IsRoot;

    /// <summary>
    /// Primary button title per step (one Filled button per screen).
    /// </summary>
    public string PrimaryText => Step switch
    {
        1 => AppStrings.WizardChooseThisFolder,
        2 => AppStrings.WizardNext,
        _ => AppStrings.WizardCreatePair,
    };

    /// <summary>
    /// Primary button enabled: step 2 waits for a picked folder, step 3 — for everything ready.
    /// </summary>
    public bool PrimaryEnabled => Step switch
    {
        2 => Local is not null,
        _ => !IsBusy,
    };

    /// <summary>
    /// Step description for TalkBack ("Step 2 of 3").
    /// </summary>
    public string StepCaption => string.Format(AppStrings.WizardStepOf, Step);

    /// <summary>
    /// Opacity of indicator dot 1 (1.0 active / 0.35 inactive).
    /// </summary>
    public double Dot1Opacity => Step == 1 ? 1 : 0.35;

    public double Dot2Opacity => Step == 2 ? 1 : 0.35;

    public double Dot3Opacity => Step == 3 ? 1 : 0.35;

    /// <summary>
    /// Step 1 section (remote browser) is visible.
    /// </summary>
    public bool IsStep1 => Step == 1;

    /// <summary>
    /// Step 2 section (local folder) is visible.
    /// </summary>
    public bool IsStep2 => Step == 2;

    /// <summary>
    /// Step 3 section (schedule) is visible.
    /// </summary>
    public bool IsStep3 => Step == 3;

    /// <summary>
    /// The picked local folder card is shown (step 2, after picking).
    /// </summary>
    public bool HasLocal => Local is not null;

    /// <summary>
    /// Loads the account and the DAV root; called by code-behind in <c>OnAppearing</c>.
    /// With no account (restart outside the gate) — show a dialog and go back.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_browser is not null)
        {
            return; // already initialized — OnAppearing may fire more than once
        }

        IsBusy = true;
        try
        {
            var list = await _accounts.ListAsync(ct);
            if (list.Count == 0)
            {
                await Application.Current!.Dispatcher.DispatchAsync(async () =>
                {
                    await _dialogs.ShowAlertAsync(AppStrings.WizardNoAccountTitle, AppStrings.WizardNoAccountBody, AppStrings.HelpOk, ct);
                    await _navigation.BackAsync(ct);
                });
                return;
            }

            _account = list[0]; // v1: a single account
            _browser = _storeFactory.CreateBrowser(_account);
            await LoadFolderAsync(RemotePath.Root, ct);
        }
        catch (OperationCanceledException)
        {
            // Left the screen mid-flight — no error.
        }
        catch (Exception ex)
        {
            // The code-behind calls LoadAsync fire-and-forget, so an unexpected failure
            // must surface inline — otherwise the wizard silently shows an empty list.
            await Application.Current!.Dispatcher.DispatchAsync(() =>
                ErrorText = string.Format(AppStrings.WizardLoadFailed, ex.Message));
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// PROPFIND of one level → directory rows (sorted by name); failures go inline.
    /// </summary>
    private async Task LoadFolderAsync(RemotePath folder, CancellationToken ct)
    {
        if (_browser is null)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            var listed = await _browser.ListAsync(folder, ct);
            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                if (listed.IsFailure)
                {
                    ErrorText = string.Format(AppStrings.WizardLoadFailed, Services.ErrorUi.Describe(listed.Error));
                    return;
                }

                _current = folder;
                Folders.Clear();
                foreach (var entry in listed.Value.Where(e => e.Kind == EntryKind.Directory).OrderBy(e => e.Path.Name, StringComparer.CurrentCulture))
                {
                    Folders.Add(new RemoteBrowserRow(entry.Path.Name, entry.Path, OpenFolderCommand(entry.Path)));
                }

                Breadcrumb = folder.IsRoot
                    ? AppStrings.WizardRootCrumb
                    : string.Join(" › ", folder.Segments);
                Raise(nameof(Current));
                RaiseStep();
                RaisePrimary();
            });
        }
        catch (OperationCanceledException)
        {
            // The wizard was closed — the state is no longer needed.
        }
        finally
        {
            // IsBusy is born here — it must die here too: otherwise navigating into a subfolder
            // spins the spinner forever and blocks PrimaryCommand/rows (canExecute = !IsBusy).
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Row command: enter a folder (IsBusy makes a repeat tap of the previous task a no-op).
    /// </summary>
    private Mvvm.AsyncRelayCommand OpenFolderCommand(RemotePath path) =>
        new(async (_, ct) => await LoadFolderAsync(path, ct), _ => !IsBusy);

    private async Task GoUpAsync()
    {
        if (!_current.IsRoot)
        {
            await LoadFolderAsync(_current.Parent, CancellationToken.None);
        }
    }

    /// <summary>
    /// Step 1 → 2: records the current folder as the future pair's remote root.
    /// </summary>
    private Task ChooseRemoteAsync()
    {
        _chosenRemote = _current;
        Step = 2;
        return Task.CompletedTask;
    }

    private async Task PickLocalAsync(CancellationToken ct)
    {
        ErrorText = null;
        IsBusy = true;
        try
        {
            var picked = await _folderPicker.PickAsync(ct); // null — cancelled by the user; state unchanged
            if (picked is not null)
            {
                await Application.Current!.Dispatcher.DispatchAsync(() => Local = picked);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed while the picker was open — no error.
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Dispatcher of the primary action per step (one Filled button per screen).
    /// </summary>
    private async Task RunPrimaryAsync(CancellationToken ct)
    {
        switch (Step)
        {
            case 1:
                await ChooseRemoteAsync();
                break;
            case 2 when Local is not null:
                Step = 3;
                break;
            case 2:
                return;
            default:
                await SaveAsync(ct);
                break;
        }
    }

    /// <summary>
    /// Step 3: save the <see cref="FolderPair"/> (config only — no file operations).
    /// Schedule — the selected chip; Conflict — KeepBoth (v1 default); Paused — false.
    /// </summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        if (_account is null || _chosenRemote is not { } remote || Local is not { } local)
        {
            ErrorText = AppStrings.WizardIncomplete;
            return;
        }

        var schedule = Schedules.FirstOrDefault(s => s.IsSelected)?.Value ?? SyncSchedule.Every15Minutes;
        // Brand as stored data: the display name is persisted in the config DB, so it must not
        // depend on the UI culture at creation time (about screen/XAML titles use AppStrings.AppDisplayName).
        var displayName = remote.IsRoot ? "FileFerry" : remote.Name;
        var pair = new FolderPair(
            Guid.NewGuid(),
            _account.Id,
            remote.ToString(),
            local.TreeUri,
            displayName,
            schedule,
            ConflictStrategy.KeepBoth,
            Paused: false,
            _wifiOnly);

        IsBusy = true;
        try
        {
            await _pairs.SaveAsync(pair, ct);
            await _scheduler.ScheduleAsync(pair.Id, schedule, ct); // a saved pair gets its periodic run right away
            // the saved schedule is the moment a transfer notification earns its place; the ask is
            // fire-and-forget — its dialog must never block the way back to the pair list, and a
            // refusal leaves the silent mode the app tolerates by design
            _ = _notifications.RequestAsync();
            await Application.Current!.Dispatcher.DispatchAsync(() => _navigation.BackAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // Canceled during the write — the pair was not saved; a repeat tap is safe (one-write UoW).
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Re-raises the step's derived properties.
    /// </summary>
    private void RaiseStep()
    {
        Raise(nameof(IsStep1));
        Raise(nameof(IsStep2));
        Raise(nameof(IsStep3));
        Raise(nameof(Dot1Opacity));
        Raise(nameof(Dot2Opacity));
        Raise(nameof(Dot3Opacity));
        Raise(nameof(StepCaption));
        Raise(nameof(CanGoUp));
        Raise(nameof(PrimaryText));
    }

    /// <summary>
    /// Re-raises the primary button's availability condition.
    /// </summary>
    private void RaisePrimary()
    {
        Raise(nameof(PrimaryEnabled));
        Raise(nameof(PrimaryText));
    }
}
