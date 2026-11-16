using System.Collections.ObjectModel;
using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the pair details: status of the last run from <see cref="ISyncStateRepository"/>,
/// pause toggle, counter sections (conflicts from run journals, stash
/// <see cref="IStashRepository"/>), recent events, interval and local-folder changes,
/// config-only deletion. No policy here — presentation and config editing only.
/// </summary>
public sealed class PairDetailsViewModel : Mvvm.ObservableObject
{
    private readonly IFolderPairRepository _pairs;
    private readonly ISyncStateRepository _state;
    private readonly IStashRepository _stash;
    private readonly ILocalFolderPicker _picker;
    private readonly INavigationService _navigation;
    private readonly IAppDialogs _dialogs;
    private readonly IClock _clock;
    private readonly StashPolicyOptions _stashOptions;
    private readonly ISyncScheduler _scheduler;
    private readonly SyncStatusService _status;

    private Guid _pairId;
    private FolderPair? _pair;
    private string _headerTitle = AppStrings.PairListTitle;
    private string _localLine = string.Empty;
    private string _lastRunLine = AppStrings.DetailsNeverRun;
    private string _nextRunLine = string.Empty;
    private string _conflictsLine = "0";
    private string _stashLine = "0";
    private string _intervalValue = string.Empty;
    private bool _pauseSync;
    private bool _isBusy;
    private bool _isRunActive;
    private string _runProgressText = AppStrings.SyncPreparing;
    private double _runRatio;
    private bool _runDeterminate;

    public PairDetailsViewModel(
        IFolderPairRepository pairs,
        ISyncStateRepository state,
        IStashRepository stash,
        ILocalFolderPicker picker,
        INavigationService navigation,
        IAppDialogs dialogs,
        IClock clock,
        StashPolicyOptions stashOptions,
        ISyncScheduler scheduler,
        SyncStatusService status)
    {
        _pairs = pairs;
        _state = state;
        _stash = stash;
        _picker = picker;
        _navigation = navigation;
        _dialogs = dialogs;
        _clock = clock;
        _stashOptions = stashOptions;
        _scheduler = scheduler;
        _status = status;

        OpenConflictsCommand = new Mvvm.AsyncRelayCommand((_, ct) => GoAsync(AppShell.ConflictsRoute, ct), _ => _pair is not null);
        OpenStashCommand = new Mvvm.AsyncRelayCommand((_, ct) => GoAsync(AppShell.StashRoute, ct), _ => _pair is not null);
        ChangeIntervalCommand = new Mvvm.AsyncRelayCommand((_, ct) => ChangeIntervalAsync(ct), _ => !IsBusy && _pair is not null);
        SyncNowCommand = new Mvvm.AsyncRelayCommand(
            (_, ct) => _pair is null ? Task.CompletedTask : _scheduler.RunNowAsync(_pairId, ct),
            _ => _pair is not null && !IsRunActive);
        ChangeFolderCommand = new Mvvm.AsyncRelayCommand((_, ct) => ChangeFolderAsync(ct), _ => !IsBusy && _pair is not null);
        DeleteCommand = new Mvvm.AsyncRelayCommand((_, ct) => DeleteAsync(ct), _ => !IsBusy && _pair is not null);
        CancelRunCommand = new Mvvm.AsyncRelayCommand(
            (_, _) =>
            {
                _status.Cancel(_pairId);
                return Task.CompletedTask;
            },
            _ => IsRunActive);
    }

    /// <summary>
    /// Events of the last run ("Pair journal" section, up to 10 rows).
    /// </summary>
    public ObservableCollection<JournalEventRow> Journal { get; } = [];

    /// <summary>
    /// Tap of the "Conflicts" section → the conflicts screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand OpenConflictsCommand { get; }

    /// <summary>
    /// Tap of the "Deleted copies" section → the trash-buffer screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand OpenStashCommand { get; }

    /// <summary>
    /// Change the interval via an action sheet (<see cref="SyncSchedule"/> values).
    /// </summary>
    public Mvvm.AsyncRelayCommand ChangeIntervalCommand { get; }

    /// <summary>
    /// Manual run: a one-shot WorkManager task; muted while a run is active.
    /// </summary>
    public Mvvm.AsyncRelayCommand SyncNowCommand { get; }

    /// <summary>
    /// Cancel the pair's active run (the signal reaches the Worker through the hub).
    /// </summary>
    public Mvvm.AsyncRelayCommand CancelRunCommand { get; }

    /// <summary>
    /// Re-pick the local folder via SAF (writes the new <c>LocalTreeKey</c>).
    /// </summary>
    public Mvvm.AsyncRelayCommand ChangeFolderCommand { get; }

    /// <summary>
    /// Delete the pair: config only, confirmation mandatory.
    /// </summary>
    public Mvvm.AsyncRelayCommand DeleteCommand { get; }

    /// <summary>
    /// AppBar title — the pair's remote path (pair details).
    /// </summary>
    public string HeaderTitle
    {
        get => _headerTitle;
        private set => Set(ref _headerTitle, value);
    }

    /// <summary>
    /// Meta line of the local folder ("Device files › …").
    /// </summary>
    public string LocalLine
    {
        get => _localLine;
        private set => Set(ref _localLine, value);
    }

    /// <summary>
    /// Last-run line: summary · relative time · event count.
    /// </summary>
    public string LastRunLine
    {
        get => _lastRunLine;
        private set => Set(ref _lastRunLine, value);
    }

    /// <summary>
    /// "When next" line: interval time, "Manual only", or "Paused".
    /// </summary>
    public string NextRunLine
    {
        get => _nextRunLine;
        private set => Set(ref _nextRunLine, value);
    }

    /// <summary>
    /// Conflict counter (Warning events of recent runs) in the conflicts screen-navigation section.
    /// </summary>
    public string ConflictsLine
    {
        get => _conflictsLine;
        private set => Set(ref _conflictsLine, value);
    }

    /// <summary>
    /// Stash copy counter + TTL note in the trash buffer screen-navigation section.
    /// </summary>
    public string StashLine
    {
        get => _stashLine;
        private set => Set(ref _stashLine, value);
    }

    /// <summary>
    /// Current interval (the wizard chip's localized label).
    /// </summary>
    public string IntervalValue
    {
        get => _intervalValue;
        private set => Set(ref _intervalValue, value);
    }

    /// <summary>
    /// Pause toggle: two-way; the repository write happens on change.
    /// </summary>
    public bool PauseSync
    {
        get => _pauseSync;
        set
        {
            if (Set(ref _pauseSync, value) && _pair is { } pair && pair.Paused != value)
            {
                _ = SaveAsync(pair with { Paused = value }, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// A network/disk read is in progress — controls are blocked.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            // Regression: Apply's raise never happens while IsBusy is
            // still true, and PropertyChanged(IsBusy) alone does not make MAUI re-query commands —
            // buttons stayed dead forever. Single chokepoint: any change of this flag
            // re-queries the gated commands.
            if (Set(ref _isBusy, value))
            {
                Mvvm.BindingMainThread.Post(() =>
                {
                    ChangeIntervalCommand.RaiseCanExecuteChanged();
                    ChangeFolderCommand.RaiseCanExecuteChanged();
                    DeleteCommand.RaiseCanExecuteChanged();
                });
            }
        }
    }

    /// <summary>
    /// This pair has an active run (hub) — the progress bar and "Cancel" are shown.
    /// </summary>
    public bool IsRunActive
    {
        get => _isRunActive;
        private set => Set(ref _isRunActive, value);
    }

    /// <summary>
    /// Progress line "File X of Y" or "Preparing…".
    /// </summary>
    public string RunProgressText
    {
        get => _runProgressText;
        private set => Set(ref _runProgressText, value);
    }

    /// <summary>
    /// Bar fill 0..1 (meaningful only when <see cref="RunDeterminate"/> is <see langword="true"/>).
    /// </summary>
    public double RunRatio
    {
        get => _runRatio;
        private set => Set(ref _runRatio, value);
    }

    /// <summary>
    /// The total was announced (<c>OnPlanned</c> fired) — show the bar; until then only "Preparing…".
    /// </summary>
    public bool RunDeterminate
    {
        get => _runDeterminate;
        private set => Set(ref _runDeterminate, value);
    }

    /// <summary>
    /// Journal is non-empty → show the list, else "No events yet".
    /// </summary>
    public bool HasJournal => Journal.Count > 0;

    /// <summary>
    /// Journal is empty (mirror of <see cref="HasJournal"/>) — no converter needed in XAML.
    /// </summary>
    public bool ShowLogEmpty => !HasJournal;

    /// <summary>
    /// Second run-status line: duration and event count (details use absolute time).
    /// </summary>
    public string LastRunDetail { get; private set; } = string.Empty;

    /// <summary>
    /// Loads the pair by id (code-behind passes the query parameter) and its state.
    /// Repeated screen appearances just reload the data.
    /// </summary>
    /// <param name="pairId">Pair taken from the route.</param>
    public async Task LoadAsync(Guid pairId, CancellationToken ct)
    {
        _pairId = pairId;
        _status.StateChanged -= OnStatusChanged; // the subscription is idempotent: OnAppearing may fire more than once
        _status.StateChanged += OnStatusChanged;
        ApplyRunStatus(_status.StatusFor(pairId));
        await ReloadAsync(ct);
    }

    /// <summary>
    /// Unsubscribes from the status hub (page.OnDisappearing) — the VM is transient, otherwise
    /// the subscription would leak past the screen.
    /// </summary>
    public void Detach() => _status.StateChanged -= OnStatusChanged;

    /// <summary>
    /// Hub event: brings the state snapshot onto the UI thread; a finished run re-reads the status grid.
    /// </summary>
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        var snapshot = _status.StatusFor(_pairId);
        Application.Current?.Dispatcher.Dispatch(() =>
        {
            ApplyRunStatus(snapshot);
            if (snapshot is null)
            {
                _ = ReloadAsync(CancellationToken.None); // the run ended — fresh run-log into the grid
            }
        });
    }

    /// <summary>
    /// Projects <see cref="PairRunStatus"/> onto the pair details progress-bar properties.
    /// </summary>
    private void ApplyRunStatus(PairRunStatus? status)
    {
        IsRunActive = status is not null;
        if (status is null)
        {
            SyncNowCommand.RaiseCanExecuteChanged();
            CancelRunCommand.RaiseCanExecuteChanged();
            return;
        }

        RunDeterminate = status.Ratio is not null;
        RunRatio = status.Ratio ?? 0;
        RunProgressText = status.ProgressLabel;
        SyncNowCommand.RaiseCanExecuteChanged();
        CancelRunCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Reloads the pair, the last 10 runs, and the conflict and stash counters.
    /// </summary>
    private async Task ReloadAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var pair = await _pairs.FindAsync(_pairId, ct);
            if (pair is null)
            {
                // the pair was deleted from another screen — honestly return to the list
                await Application.Current!.Dispatcher.DispatchAsync(async () =>
                {
                    await _dialogs.ShowAlertAsync(AppStrings.DetailsGoneTitle, AppStrings.DetailsGoneBody, AppStrings.HelpOk, ct);
                    await _navigation.BackAsync(ct);
                });
                return;
            }

            var runs = await _state.RecentRunsAsync(_pairId, 20, ct);
            var stashCount = (await _stash.ListAsync(_pairId, ct)).Count;
            var now = _clock.UtcNow;

            await Application.Current!.Dispatcher.DispatchAsync(() => Apply(pair, runs, stashCount, now));
        }
        catch (OperationCanceledException)
        {
            // the screen was closed while reading — the state is no longer needed
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Projects the loaded state onto the properties (UI thread only).
    /// </summary>
    private void Apply(FolderPair pair, IReadOnlyList<SyncRunLog> runs, int stashCount, DateTimeOffset now)
    {
        _pair = pair;
        // Regression: MAUI gestures/buttons cache CanExecute at binding
        // time — before LoadAsync _pair is still null, so "Conflicts/Stash/…" stayed dead forever.
        // The first Apply force re-queries every canExecute that depends on the pair.
        OpenConflictsCommand.RaiseCanExecuteChanged();
        OpenStashCommand.RaiseCanExecuteChanged();
        ChangeIntervalCommand.RaiseCanExecuteChanged();
        SyncNowCommand.RaiseCanExecuteChanged();
        CancelRunCommand.RaiseCanExecuteChanged();
        ChangeFolderCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        HeaderTitle = pair.RemoteRootPath;
        var localName = FolderPair.LocalFolderName(pair.LocalTreeKey);
        LocalLine = $"{AppStrings.PairCardLocalPrefix} › {(localName.Length > 0 ? localName : pair.DisplayName)}";

        IntervalValue = ScheduleLabel(pair.Schedule);
        _pauseSync = pair.Paused; // system load, not a user change — no write-back
        Raise(nameof(PauseSync));

        var latest = runs.Count > 0 ? runs[0] : null;
        LastRunLine = latest is null
            ? AppStrings.DetailsNeverRun
            : $"{OutcomeText(latest.Outcome)} · {Humanize.Ago(latest.StartedAt, now)}";
        LastRunDetail = latest is null
            ? string.Empty
            : $"{string.Format(AppStrings.DetailsEventsCount, latest.Events.Count)}"
                + (latest.FinishedAt is { } finish
                    ? $" · {string.Format(AppStrings.DetailsDurationLabel, Humanize.Duration(finish - latest.StartedAt))}"
                    : string.Empty);
        NextRunLine = NextRunText(pair, latest, now);

        // Counter rule = ConflictsPage: Warning events that carry a file (no service Warnings).
        ConflictsLine = runs.Sum(r => r.Events.Count(
            e => e.Outcome == SyncEventOutcome.Warning && e.Path is { IsEmpty: false })).ToString();
        StashLine = stashCount > 0
            ? $"{stashCount} · {string.Format(AppStrings.DetailsStashNote, (int)_stashOptions.Ttl.TotalDays)}"
            : string.Format(AppStrings.DetailsStashNote, (int)_stashOptions.Ttl.TotalDays);

        Journal.Clear();
        if (latest is not null)
        {
            foreach (var e in latest.Events.TakeLast(10))
            {
                Journal.Add(JournalEventRow.From(e));
            }
        }

        Raise(nameof(HasJournal));
        Raise(nameof(ShowLogEmpty)); // XAML watches ShowLogEmpty specifically; without it "No events yet" never hides
    }

    /// <summary>
    /// "When next": paused → "Paused", Manual → "Manual only", else ≈ finish + interval
    /// (details use absolute time).
    /// </summary>
    private static string NextRunText(FolderPair pair, SyncRunLog? latest, DateTimeOffset now) =>
        pair.Paused
            ? AppStrings.DetailsNextPaused
            : pair.Schedule switch
            {
                SyncSchedule.Manual => AppStrings.DetailsNextManual,
                _ => string.Format(
                    AppStrings.DetailsNextAt,
                    ((latest?.FinishedAt ?? latest?.StartedAt ?? now) + ScheduleInterval(pair.Schedule)).ToLocalTime().ToString("HH:mm")),
            };

    /// <summary>
    /// Minute values of the schedule enum (0 — manual, not used here).
    /// </summary>
    private static TimeSpan ScheduleInterval(SyncSchedule schedule) => TimeSpan.FromMinutes((int)schedule);

    /// <summary>
    /// Run summary per the microcopy; success uses the same text as the pair list chip.
    /// </summary>
    private static string OutcomeText(SyncRunOutcome outcome) => outcome switch
    {
        SyncRunOutcome.Running => AppStrings.OutcomeRunning,
        SyncRunOutcome.Success => AppStrings.StatusSuccess,
        SyncRunOutcome.Partial => AppStrings.OutcomePartial,
        SyncRunOutcome.Failed => AppStrings.OutcomeFailed,
        _ => AppStrings.OutcomeCancelled,
    };

    /// <summary>
    /// Interval label — the same one the wizard chips use (vocabulary consistency).
    /// </summary>
    private static string ScheduleLabel(SyncSchedule schedule) =>
        ChoiceOption.KnownSchedules().First(k => k.Value == schedule).Label;

    /// <summary>
    /// Navigation to a section screen with the pair id in the query.
    /// </summary>
    private Task GoAsync(string route, CancellationToken ct) =>
        _navigation.GoAsync($"{route}?id={_pairId:N}", ct);

    /// <summary>
    /// Action sheet of intervals → store the new <see cref="SyncSchedule"/> (config only).
    /// </summary>
    private async Task ChangeIntervalAsync(CancellationToken ct)
    {
        if (_pair is not { } pair)
        {
            return;
        }

        var known = ChoiceOption.KnownSchedules();
        var choice = await _dialogs.ShowActionAsync(
            AppStrings.DetailsIntervalTitle, AppStrings.DialogCancel, known.Select(k => k.Label).ToList(), ct);
        if (choice >= 0 && known[choice].Value != pair.Schedule)
        {
            await SaveAsync(pair with { Schedule = known[choice].Value }, ct);
        }
    }

    /// <summary>
    /// SAF folder re-pick: a new persistable tree-URI in <c>LocalTreeKey</c>; the pair's baseline is untouched.
    /// </summary>
    private async Task ChangeFolderAsync(CancellationToken ct)
    {
        if (_pair is not { } pair)
        {
            return;
        }

        var picked = await _picker.PickAsync(ct);
        if (picked is not null)
        {
            await SaveAsync(pair with { LocalTreeKey = picked.TreeUri }, ct);
        }
    }

    /// <summary>
    /// Delete the pair config with confirmation; files and the stash remain (dialog).
    /// </summary>
    private async Task DeleteAsync(CancellationToken ct)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.DetailsDeletePair, AppStrings.DetailsDeletePairBody, AppStrings.DeletePairAccept, AppStrings.DialogCancel, ct);
        if (confirmed)
        {
            await _pairs.DeleteAsync(_pairId, ct);
            await _navigation.BackAsync(ct);
        }
    }

    /// <summary>
    /// Single config write + scheduling sync + reload (one UoW per change).
    /// </summary>
    private async Task SaveAsync(FolderPair pair, CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await _pairs.SaveAsync(pair, ct);
            if (pair.Paused)
            {
                await _scheduler.CancelAsync(pair.Id, ct);
            }
            else
            {
                await _scheduler.ScheduleAsync(pair.Id, pair.Schedule, ct);
            }

            await ReloadAsync(ct);
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }
}
