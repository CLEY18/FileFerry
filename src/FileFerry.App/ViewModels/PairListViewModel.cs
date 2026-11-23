using System.Collections.ObjectModel;
using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the pair list: list of pairs (active on top), account dns chip,
/// pull-to-refresh ("check and sync everything" — a live run awaits the engine), FAB "add",
/// card context menu (pause / sync now / delete). All logic lives here.
/// </summary>
public sealed class PairListViewModel : Mvvm.ObservableObject
{
    private readonly IFolderPairRepository _pairs;
    private readonly IAccountRepository _accounts;
    private readonly ISyncStateRepository _state;
    private readonly INavigationService _navigation;
    private readonly IAppDialogs _dialogs;
    private readonly ISyncScheduler _scheduler;
    private readonly SyncStatusService _status;
    private readonly HashSet<Guid> _running = []; // pairs with an active run per the hub's last event
    private string _accountLabel = AppStrings.AccountChipEmpty;
    private bool _isInsecure;

    private bool _gatePending;
    private bool _isRefreshing;

    public PairListViewModel(
        IFolderPairRepository pairs,
        IAccountRepository accounts,
        ISyncStateRepository state,
        INavigationService navigation,
        IAppDialogs dialogs,
        ISyncScheduler scheduler,
        SyncStatusService status)
    {
        _pairs = pairs;
        _accounts = accounts;
        _state = state;
        _navigation = navigation;
        _dialogs = dialogs;
        _scheduler = scheduler;
        _status = status;

        RefreshCommand = new Mvvm.AsyncRelayCommand(RefreshAsync);
        AddCommand = new Mvvm.AsyncRelayCommand(ct => _navigation.GoAsync(AppShell.PairWizardRoute, ct));
        AccountCommand = new Mvvm.AsyncRelayCommand(
            (_, ct) => _navigation.GoAsync(AppShell.SettingsRoute, ct)); // chip → the settings screen with account and logout
        CardCommand = new Mvvm.AsyncRelayCommand(
            async (param, ct) =>
            {
                if (param is PairListItem card)
                {
                    await _navigation.GoAsync($"{AppShell.PairDetailsRoute}?id={card.Id:N}", ct);
                }
            },
            param => param is PairListItem);
        CardMenuCommand = new Mvvm.AsyncRelayCommand(ShowCardMenuAsync, param => param is PairListItem && !IsBusy);
        PauseCommand = new Mvvm.AsyncRelayCommand(async (param, ct) => await TogglePauseAsync(((PairListItem)param!).Id, ct), param => param is PairListItem && !IsBusy);
        SyncNowCommand = new Mvvm.AsyncRelayCommand(
            async (param, ct) =>
            {
                if (param is PairListItem card)
                {
                    await _scheduler.RunNowAsync(card.Id, ct);
                }
            },
            param => param is PairListItem);
        DeleteCommand = new Mvvm.AsyncRelayCommand(async (param, ct) => await DeleteAsync(((PairListItem)param!).Id, ct), param => param is PairListItem && !IsBusy);
    }

    /// <summary>
    /// List cards (sorting: active before paused).
    /// </summary>
    public ObservableCollection<PairListItem> Items { get; } = [];

    /// <summary>
    /// Text of the account dns chip in the header (pair list).
    /// </summary>
    public string AccountLabel
    {
        get => _accountLabel;
        private set => Set(ref _accountLabel, value);
    }

    /// <summary>
    /// The account is reachable only in cleartext — the header wears the "unencrypted" badge
    /// (a LAN server without TLS is legitimate, but the user must see the price every screen).
    /// </summary>
    public bool IsInsecure
    {
        get => _isInsecure;
        private set => Set(ref _isInsecure, value);
    }

    /// <summary>
    /// List is empty — show EmptyState.
    /// </summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Data refresh in progress — RefreshView indicator; blocks card actions.
    /// </summary>
    public bool IsBusy
    {
        get => _isRefreshing;
        private set => Set(ref _isRefreshing, value);
    }

    /// <summary>
    /// Pull-to-refresh = reload state; "sync everything" awaits the engine.
    /// </summary>
    public Mvvm.AsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// FAB "add pair" → the add-pair wizard.
    /// </summary>
    public Mvvm.AsyncRelayCommand AddCommand { get; }

    /// <summary>
    /// Tap on the account dns chip → the account screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand AccountCommand { get; }

    /// <summary>
    /// Card tap → the pair details screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand CardCommand { get; }

    /// <summary>
    /// Card long-press / ⋮ → context menu of actions (duplicated in Pair Details).
    /// </summary>
    public Mvvm.AsyncRelayCommand CardMenuCommand { get; }

    /// <summary>
    /// Card menu: pause/resume (writes <c>Paused</c> to the repository).
    /// </summary>
    public Mvvm.AsyncRelayCommand PauseCommand { get; }

    /// <summary>
    /// Card menu: sync now (a manual run).
    /// </summary>
    public Mvvm.AsyncRelayCommand SyncNowCommand { get; }

    /// <summary>
    /// Card menu: delete the config only (confirmation is mandatory).
    /// </summary>
    public Mvvm.AsyncRelayCommand DeleteCommand { get; }

    /// <summary>
    /// Reload the account chip and the list (OnAppearing + pull-to-refresh).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _status.StateChanged -= OnStatusChanged; // the subscription is idempotent: OnAppearing may fire more than once
        _status.StateChanged += OnStatusChanged;
        IsBusy = true;
        try
        {
            await ReloadAsync(ct);
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Pair actions: menu; deletion — config only and only with confirmation.
    /// </summary>
    private async Task ShowCardMenuAsync(object? item, CancellationToken ct)
    {
        if (item is not PairListItem card)
        {
            return;
        }

        var pauseLabel = card.IsPaused ? AppStrings.CardActionResume : AppStrings.CardActionPause;
        var choice = await _dialogs.ShowActionAsync(
            card.RemotePath,
            AppStrings.DialogCancel,
            [pauseLabel, AppStrings.SyncNowAction, AppStrings.CardActionDelete],
            ct);
        switch (choice)
        {
            case 0:
                await TogglePauseAsync(card.Id, ct);
                break;
            case 1:
                await _scheduler.RunNowAsync(card.Id, ct); // one-shot unique job with Replace
                break;
            case 2:
                await DeleteAsync(card.Id, ct);
                break;
        }
    }

    private async Task TogglePauseAsync(Guid pairId, CancellationToken ct)
    {
        var pair = await _pairs.FindAsync(pairId, ct);
        if (pair is not null)
        {
            var paused = !pair.Paused;
            await _pairs.SaveAsync(pair with { Paused = paused }, ct);
            if (paused)
            {
                await _scheduler.CancelAsync(pairId, ct);
            }
            else
            {
                await _scheduler.ScheduleAsync(pairId, pair.Schedule, ct);
            }

            await ReloadAsync(ct);
        }
    }

    private async Task DeleteAsync(Guid pairId, CancellationToken ct)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.DeletePairTitle, AppStrings.DeletePairBody, AppStrings.DeletePairAccept, AppStrings.DialogCancel, ct, destructive: true);
        if (confirmed)
        {
            await _scheduler.CancelAsync(pairId, ct); // cancel periodic/one-shot jobs before deleting the config
            await _pairs.DeleteAsync(pairId, ct); // config-only delete; files and the stash are untouched
            await ReloadAsync(ct);
        }
    }

    /// <summary>
    /// Loads the account chip and the cards; the collection is updated only from the UI thread.
    /// </summary>
    private async Task ReloadAsync(CancellationToken ct = default)
    {
        var accounts = await _accounts.ListAsync(ct);
        var account = accounts.Count > 0 ? accounts[0] : null;
        var pairs = await _pairs.ListAsync(ct);
        // Status chip "status always visible": summary of the last completed run +
        // conflicting Warning events; the rule matches ConflictsPage (Warning + path, window of 20,
        // resolutions acknowledged).
        var runWindows = await Task.WhenAll(
            pairs.Select(pair => _state.RecentRunsAsync(pair.Id, 20, ct)));

        var items = pairs
            .Zip(runWindows, (pair, runs) =>
                PairListItem.From(pair, StatusFromRuns(pair, runs), Services.ConflictEvents.CountUnresolved(runs)))
            .Select(p => p with
            {
                // MAUI 10 has no ancestor bindings in templates — the card carries the VM's commands itself.
                TapCommand = CardCommand,
                MenuCommand = CardMenuCommand,
                PauseCommand = PauseCommand,
                SyncNowCommand = SyncNowCommand,
                DeleteCommand = DeleteCommand,
            })
            .Select(WithRunStatus) // active run: progress bar + "File X of Y" right on the card
            .OrderBy(item => item.IsPaused) // stable OrderBy: "active on top", the rest by creation order
            .ToList();

        await Application.Current!.Dispatcher.DispatchAsync(async () =>
        {
            var accountUri = account is null ? null : new Uri(account.BaseUrl);
            AccountLabel = account is null
                ? AppStrings.AccountChipEmpty
                : $"{account.UserName}@{accountUri!.Host}";
            IsInsecure = accountUri?.Scheme == Uri.UriSchemeHttp;
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            Raise(nameof(IsEmpty));

            // Without a server connection this screen has nothing to show and every action
            // on it dead-ends — send the user to the welcome screen instead of an empty shell.
            // Once per screen visit: the appearing hook can fire more than once in a row.
            if (account is null && !_gatePending)
            {
                _gatePending = true;
                await _navigation.GoAsync(AppShell.WelcomeRoute, CancellationToken.None);
            }
        });
    }

    /// <summary>
    /// Unsubscribes from the status hub (page.OnDisappearing) — the VM is transient, otherwise
    /// the subscription would outlive the screen.
    /// </summary>
    public void Detach() => _status.StateChanged -= OnStatusChanged;

    /// <summary>
    /// Hub event: refresh the card progress bars; replacing the record in the collection rebinds the cell.
    /// </summary>
    private void OnStatusChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Dispatch(() =>
        {
            // A run finished (was active → gone): the "Synced/Error" chip lives in the DB logs,
            // so on completion we reload the cards in full (they once showed "Waiting" forever).
            var wasRunning = _running.Count;
            _running.Clear();
            foreach (var card in Items)
            {
                if (_status.StatusFor(card.Id) is not null)
                {
                    _running.Add(card.Id);
                }
            }

            for (var i = 0; i < Items.Count; i++)
            {
                var updated = WithRunStatus(Items[i]);
                if (!updated.Equals(Items[i]))
                {
                    Items[i] = updated;
                }
            }

            if (_running.Count == 0 && wasRunning > 0 && !_isRefreshing)
            {
                _ = RefreshAsync(); // the summary of the finished run comes from the repository
            }
        });

    /// <summary>
    /// Projects the last completed run's summary onto the status chip.
    /// </summary>
    private static PairStatus StatusFromRuns(FolderPair pair, IReadOnlyList<SyncRunLog> runs)
    {
        if (pair.Paused)
        {
            return PairStatus.Idle; // "Paused" is Idle, not a separate chip
        }

        var conflicts = Services.ConflictEvents.CountUnresolved(runs);
        var latest = runs.FirstOrDefault(r => r.Outcome is not (SyncRunOutcome.Running or SyncRunOutcome.Cancelled));
        return latest?.Outcome switch
        {
            SyncRunOutcome.Failed or SyncRunOutcome.Partial => PairStatus.Error,
            SyncRunOutcome.Success => conflicts > 0 ? PairStatus.Conflict : PairStatus.Success,
            _ => conflicts > 0 ? PairStatus.Conflict : PairStatus.Idle,
        };
    }

    /// <summary>
    /// Projects the pair's active run onto the card (progress bar + line).
    /// </summary>
    private PairListItem WithRunStatus(PairListItem card)
    {
        var run = _status.StatusFor(card.Id);
        if (run is null)
        {
            return card; // no active run — the chip stays from the last completed run's summary
        }

        return card with
        {
            Status = PairStatus.Syncing,
            StatusText = AppStrings.StatusSyncing,
            IsSyncing = true,
            Progress = run.Ratio ?? 0,
            ProgressText = run.ProgressLabel,
        };
    }
}
