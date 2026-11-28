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
    private bool _pauseSavePending;
    private bool? _pauseDesired;
    private bool _pauseUiOnly;
    private bool _runWasActive;
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
        ChangeFolderCommand = new Mvvm.AsyncRelayCommand(
            (_, ct) => ChangeFolderAsync(ct), _ => !IsBusy && !IsRunActive && _pair is not null);
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
    /// Conflict counter (unresolved conflict events of recent runs — the shared rule) shown in
    /// the conflicts screen-navigation section.
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
    /// Pause toggle: two-way; the last user choice wins and persists serialized — a change
    /// arriving mid-save is queued, never interleaved with the running repository/scheduler pair.
    /// </summary>
    public bool PauseSync
    {
        get => _pauseSync;
        set
        {
            if (_pauseUiOnly)
            {
                Set(ref _pauseSync, value); // a projection/revert, not the user's hand — no write-back
                return;
            }

            if (Set(ref _pauseSync, value) && _pair is { } pair && pair.Paused != value)
            {
                _pauseDesired = value;
                _ = ApplyPauseDesiredAsync();
            }
        }
    }

    /// <summary>
    /// The pause switch is free (no persistence in flight) — the view gates the toggle on it,
    /// so rapid double taps cannot overtake each other's write.
    /// </summary>
    public bool IsPauseReady => !_pauseSavePending;

    /// <summary>
    /// Persists the newest pause choice until the stored state matches it: one save at a time,
    /// a choice made mid-save lands as the next save, a failed save reverts the switch to the
    /// stored truth (a switch that lies about the schedule is worse than one that moves back).
    /// </summary>
    private async Task ApplyPauseDesiredAsync()
    {
        if (_pauseSavePending)
        {
            return; // the running loop re-reads _pauseDesired after each pass
        }

        _pauseSavePending = true;
        Raise(nameof(IsPauseReady));
        try
        {
            while (_pauseDesired is { } desired && _pair is { } pair && pair.Paused != desired)
            {
                _pauseDesired = null;
                try
                {
                    await SavePauseAsync(pair with { Paused = desired }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _pauseDesired = null; // the stored state never moved — stop, revert, say so
                    var stored = pair.Paused;
                    _pauseUiOnly = true;
                    try
                    {
                        Set(ref _pauseSync, stored);
                    }
                    finally
                    {
                        _pauseUiOnly = false;
                    }

                    var reason = Services.ErrorUi.Describe(
                        Core.Domain.SyncError.Of(Core.Domain.ErrorKind.Unknown, ex.Message));
                    await Application.Current!.Dispatcher.DispatchAsync(() => _dialogs.ShowAlertAsync(
                        AppStrings.ErrorAlertTitle, reason, AppStrings.HelpOk, CancellationToken.None));
                }
            }
        }
        finally
        {
            _pauseSavePending = false;
            Raise(nameof(IsPauseReady));
        }
    }

    /// <summary>
    /// Pause-only save: write plus scheduler swap, then a reload that keeps the switch under
    /// the user's hand (the generic reload would re-read the stored value mid-loop).
    /// </summary>
    private async Task SavePauseAsync(FolderPair pair, CancellationToken ct)
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

            await ReloadAsync(ct, rebindPause: false);
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
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
        _runWasActive = _status.StatusFor(pairId) is not null; // the edge is measured from here
        ApplyRunStatus(_status.StatusFor(pairId));
        await ReloadAsync(ct);
    }

    /// <summary>
    /// Unsubscribes from the status hub (page.OnDisappearing) — the VM is transient, otherwise
    /// the subscription would leak past the screen.
    /// </summary>
    public void Detach() => _status.StateChanged -= OnStatusChanged;

    /// <summary>
    /// Hub event: brings the state snapshot onto the UI thread; the grid re-reads only when
    /// THIS pair's run just ended — a foreign pair's tick leaves the idle screen untouched
    /// (it used to cost three repository reads and a busy flicker per tick).
    /// </summary>
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        var snapshot = _status.StatusFor(_pairId);
        var justFinished = _runWasActive && snapshot is null;
        _runWasActive = snapshot is not null;
        Application.Current?.Dispatcher.Dispatch(() =>
        {
            ApplyRunStatus(snapshot);
            if (justFinished)
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
        // rebinding the folder while a run projects its baseline would race the wipe — re-query the gate
        ChangeFolderCommand.RaiseCanExecuteChanged();
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
    /// <param name="rebindPause">Whether the switch re-reads the stored value; a pending
    /// user choice must survive a reload fired by its own save.</param>
    private async Task ReloadAsync(CancellationToken ct, bool rebindPause = true)
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

            await Application.Current!.Dispatcher.DispatchAsync(() => Apply(pair, runs, stashCount, now, rebindPause));
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
    private void Apply(FolderPair pair, IReadOnlyList<SyncRunLog> runs, int stashCount, DateTimeOffset now, bool rebindPause = true)
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
        if (rebindPause)
        {
            _pauseSync = pair.Paused; // system load, not a user change — no write-back
            Raise(nameof(PauseSync));
        }

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

        // the one counting rule every screen shares — resolved conflicts stop here too,
        // or the numbers on this screen and the conflicts screen diverge after a resolution
        ConflictsLine = ConflictEvents.CountUnresolved(runs).ToString();
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
    /// Choice dialog of intervals with the pair's current one marked → store the new
    /// <see cref="SyncSchedule"/> (config only).
    /// </summary>
    private async Task ChangeIntervalAsync(CancellationToken ct)
    {
        if (_pair is not { } pair)
        {
            return;
        }

        var known = ChoiceOption.KnownSchedules();
        var choice = await _dialogs.ShowChoiceAsync(
            AppStrings.DetailsIntervalTitle, known.Select(k => k.Label).ToList(), ChoiceOption.IndexOf(pair.Schedule), ct);
        if (choice >= 0 && known[choice].Value != pair.Schedule)
        {
            await SaveAsync(pair with { Schedule = known[choice].Value }, ct);
        }
    }

    /// <summary>
    /// SAF folder re-pick with confirmation: a new persistable tree-URI in <c>LocalTreeKey</c>.
    /// The baseline and the write-ahead journal are cleared first, so the next run reads the new
    /// folder as the truth instead of reading every difference from the old folder as a deletion.
    /// Blocked while a run is active — its projection would overwrite the cleared state.
    /// </summary>
    private async Task ChangeFolderAsync(CancellationToken ct)
    {
        if (_pair is not { } pair)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.DetailsChangeFolderTitle, AppStrings.DetailsChangeFolderBody,
            AppStrings.DetailsChangeFolderAccept, AppStrings.DialogCancel, ct, destructive: true);
        if (!confirmed)
        {
            return;
        }

        var picked = await _picker.PickAsync(ct);
        if (picked is null || picked.TreeUri == pair.LocalTreeKey)
        {
            return; // nothing chosen, or the same folder — the state stays exactly as it was
        }

        IsBusy = true;
        try
        {
            // cancel the schedule before wiping: a periodic run must not walk into the half-changed state;
            // SaveAsync re-arms it for the new tree. Wiping before saving the pair is the safe order:
            // an interrupted change leaves the old folder with an empty baseline — an honest rescan,
            // never a deletion storm built on the old tree's ghost
            await _scheduler.CancelAsync(_pairId, ct);
            await _state.ClearJournalAsync(_pairId, ct); // resume records name paths of the old tree — dead operations after the rebind
            await _state.ReplaceBaselineAsync(_pairId, [], ct);
            await SaveAsync(pair with { LocalTreeKey = picked.TreeUri }, ct);
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Delete the pair config with confirmation; files and the stash remain (dialog).
    /// </summary>
    private async Task DeleteAsync(CancellationToken ct)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.DetailsDeletePair, AppStrings.DetailsDeletePairBody, AppStrings.DeletePairAccept, AppStrings.DialogCancel, ct, destructive: true);
        if (confirmed)
        {
            await _scheduler.CancelAsync(_pairId, ct); // cancel periodic/one-shot jobs before deleting the config
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
