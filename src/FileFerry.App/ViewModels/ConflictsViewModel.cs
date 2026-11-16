using System.Collections.ObjectModel;
using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the conflicts screen: v1 conflicts (KeepBoth) do not "hang" — they are applied
/// as Warning events of run journals, so the screen shows them as a matter of fact. The fallback
/// actions "keep server/local version" are physical file operations that require the engine.
/// </summary>
public sealed class ConflictsViewModel : Mvvm.ObservableObject
{
    private readonly ISyncStateRepository _state;
    private readonly IClock _clock;
    private readonly IAppDialogs _dialogs;
    private bool _isEmpty = true;

    public ConflictsViewModel(ISyncStateRepository state, IClock clock, IAppDialogs dialogs)
    {
        _state = state;
        _clock = clock;
        _dialogs = dialogs;

        KeepServerCommand = new Mvvm.AsyncRelayCommand((_, _) => PendingAsync(AppStrings.ConflictKeepServer));
        KeepLocalCommand = new Mvvm.AsyncRelayCommand((_, _) => PendingAsync(AppStrings.ConflictKeepLocal));
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
    /// "Keep server version" action (awaits the engine). Passes the whole row as the parameter.
    /// </summary>
    public Mvvm.AsyncRelayCommand KeepServerCommand { get; }

    /// <summary>
    /// "Keep local version" action (awaits the engine).
    /// </summary>
    public Mvvm.AsyncRelayCommand KeepLocalCommand { get; }

    /// <summary>
    /// Load: the last 20 runs → Warning events with a path, freshest on top.
    /// </summary>
    /// <param name="pairId">Pair taken from the route.</param>
    public async Task LoadAsync(Guid pairId, CancellationToken ct)
    {
        try
        {
            var runs = await _state.RecentRunsAsync(pairId, 20, ct);
            var now = _clock.UtcNow;
            var rows = runs
                .SelectMany(r => r.Events)
                .Where(e => e.Outcome == SyncEventOutcome.Warning && e.Path is { IsEmpty: false })
                .OrderByDescending(e => e.At)
                .Select(e => new ConflictRow(
                    e.Path!.ToString(),
                    Humanize.Ago(e.At, now),
                    Services.JournalPhrases.Render(e),
                    AppStrings.ConflictKeptBoth,
                    KeepServerCommand,
                    KeepLocalCommand))
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
    /// Temporary: the fallback actions require the pair file engine.
    /// </summary>
    private Task PendingAsync(string feature) => _dialogs.ShowAlertAsync(
        AppStrings.PendingSectionTitle, string.Format(AppStrings.PendingSectionBody, feature), AppStrings.HelpOk, CancellationToken.None);
}
