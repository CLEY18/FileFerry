using System.Collections.ObjectModel;
using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the conflicts screen: KeepBoth conflicts never "hang" — the run applied them as
/// Warning events and both versions are safe. Each row carries the two keep-side actions; a tap
/// confirms, then runs the engine's keep-side resolution (lock-guarded, stash-first). A
/// successful resolution acknowledges the row: it turns into its "kept …" status and stops
/// counting on the pair card.
/// </summary>
public sealed class ConflictsViewModel : Mvvm.ObservableObject
{
    private readonly ISyncStateRepository _state;
    private readonly IClock _clock;
    private readonly IAppDialogs _dialogs;
    private readonly SyncRun _syncRun;
    private Guid _pairId;
    private bool _isEmpty = true;

    public ConflictsViewModel(ISyncStateRepository state, IClock clock, IAppDialogs dialogs, SyncRun syncRun)
    {
        _state = state;
        _clock = clock;
        _dialogs = dialogs;
        _syncRun = syncRun;

        KeepServerCommand = new Mvvm.AsyncRelayCommand(
            (row, ct) => ResolveAsync(row as ConflictRow, KeepSide.Server, ct));
        KeepLocalCommand = new Mvvm.AsyncRelayCommand(
            (row, ct) => ResolveAsync(row as ConflictRow, KeepSide.Local, ct));
    }

    /// <summary>
    /// Conflict rows (Warning events of recent runs), newest first.
    /// </summary>
    public ObservableCollection<ConflictRow> Items { get; } = [];

    /// <summary>
    /// List is empty → reassuring EmptyState.
    /// </summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    /// <summary>
    /// "Keep server version" action (confirmation → engine resolution). Passes the whole row.
    /// </summary>
    public Mvvm.AsyncRelayCommand KeepServerCommand { get; }

    /// <summary>
    /// "Keep local version" action (confirmation → engine resolution).
    /// </summary>
    public Mvvm.AsyncRelayCommand KeepLocalCommand { get; }

    /// <summary>
    /// Load: the last 20 runs → Warning events with a path, freshest on top; rows a later
    /// keep-side resolution already acknowledged render their kept side instead of actions.
    /// </summary>
    /// <param name="pairId">Pair taken from the route.</param>
    public async Task LoadAsync(Guid pairId, CancellationToken ct)
    {
        _pairId = pairId;
        try
        {
            var runs = await _state.RecentRunsAsync(pairId, 20, ct);
            var now = _clock.UtcNow;
            var resolutions = ConflictEvents.Resolutions(runs);
            var rows = runs
                .SelectMany(r => r.Events)
                .Where(ConflictEvents.IsConflictRow)
                .OrderByDescending(e => e.At)
                .Select(e =>
                {
                    var acknowledged =
                        resolutions.TryGetValue(e.Path!, out var resolution) && resolution.At > e.At
                            ? resolution
                            : null;
                    return new ConflictRow(
                        e.Path!,
                        e.Path!.ToString(),
                        Humanize.Ago(e.At, now),
                        Services.JournalPhrases.Render(e),
                        acknowledged is null
                            ? AppStrings.ConflictKeptBoth
                            : Services.JournalPhrases.Render(acknowledged),
                        KeepServerCommand,
                        KeepLocalCommand,
                        IsResolved: acknowledged is not null);
                })
                .ToList();

            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                IsEmpty = Items.Count == 0;
            });
        }
        catch (OperationCanceledException)
        {
            // the screen was closed while reading
        }
    }

    /// <summary>
    /// Keep-side resolution: confirm → engine (a busy pair is refused by the run lock, a
    /// resolved-elsewhere row answers "not found") → honest result dialog → reload the list.
    /// </summary>
    private async Task ResolveAsync(ConflictRow? row, KeepSide side, CancellationToken ct)
    {
        if (row is null || row.IsResolved)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.ConflictResolveConfirmTitle,
            string.Format(
                side == KeepSide.Server
                    ? AppStrings.ConflictResolveServerConfirmFormat
                    : AppStrings.ConflictResolveLocalConfirmFormat,
                row.PathText),
            side == KeepSide.Server ? AppStrings.ConflictKeepServer : AppStrings.ConflictKeepLocal,
            AppStrings.DialogCancel,
            ct);
        if (!confirmed)
        {
            return;
        }

        var result = await _syncRun.ResolveConflictAsync(_pairId, row.Path, side, ct);
        if (result.IsFailure)
        {
            await _dialogs.ShowAlertAsync(
                AppStrings.ConflictResolveFailedTitle, Services.ErrorUi.Describe(result.Error), AppStrings.HelpOk, ct);
            return;
        }

        if (result.Value.Outcome != SyncRunOutcome.Success)
        {
            await _dialogs.ShowAlertAsync(
                AppStrings.ConflictResolveFailedTitle, AppStrings.ConflictResolvePartialBody, AppStrings.HelpOk, ct);
            return;
        }

        // the resolved pair needs no confirmation tap - the list itself changes and the notice
        // says what happened; dialogs stay for the outcomes that genuinely stop the user
        await _dialogs.NotifyAsync(AppStrings.ConflictResolvedBody, ct);
        await LoadAsync(_pairId, CancellationToken.None);
    }
}
