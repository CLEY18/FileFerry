using System.Collections.ObjectModel;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the global log: events of recent runs of all pairs, merged by time;
/// filter chips "all / conflicts / errors". No auto-scroll — the list is static after load.
/// </summary>
public sealed class LogViewModel : Mvvm.ObservableObject
{
    private const string AllKey = "all";
    private const string WarningKey = "warning";
    private const string ErrorKey = "error";
    private const int RunsPerPair = 10;
    private const int RowCap = 200;

    private readonly IFolderPairRepository _pairs;
    private readonly ISyncStateRepository _state;
    private IReadOnlyList<LogRow> _allRows = [];
    private bool _isEmpty = true;

    public LogViewModel(IFolderPairRepository pairs, ISyncStateRepository state)
    {
        _pairs = pairs;
        _state = state;

        Filters =
        [
            new LogFilterChip(AllKey, AppStrings.LogFilterAll),
            new LogFilterChip(WarningKey, AppStrings.DetailsConflictsTitle),
            new LogFilterChip(ErrorKey, AppStrings.LogFilterErrors),
        ];
        Filters[0].IsSelected = true;
        foreach (var chip in Filters)
        {
            var key = chip.Key;
            chip.SelectCommand = new Mvvm.AsyncRelayCommand((_, _) =>
            {
                foreach (var other in Filters)
                {
                    other.IsSelected = ReferenceEquals(other, chip);
                }

                ApplyFilter(key);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Filter chips of the global log; exactly one is active.
    /// </summary>
    public ObservableCollection<LogFilterChip> Filters { get; }

    /// <summary>
    /// Rows shown after filtering (newest on top).
    /// </summary>
    public ObservableCollection<LogRow> Items { get; } = [];

    /// <summary>
    /// Empty journal — calm EmptyState.
    /// </summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    /// <summary>
    /// Load: the 10 latest runs per pair, events descending by time, capped at 200 rows.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var pairs = await _pairs.ListAsync(ct);
            var merged = new List<(DateTimeOffset At, LogRow Row)>();
            foreach (var pair in pairs)
            {
                var runs = await _state.RecentRunsAsync(pair.Id, RunsPerPair, ct);
                merged.AddRange(runs.SelectMany(r => r.Events).Select(e => (e.At, Row(pair, e))));
            }

            _allRows = merged.OrderByDescending(x => x.At).Take(RowCap).Select(x => x.Row).ToList();
            await Application.Current!.Dispatcher.DispatchAsync(() => ApplyFilter(CurrentFilterKey));
        }
        catch (OperationCanceledException)
        {
            // the screen was closed while reading
        }
    }

    /// <summary>
    /// Key of the active filter (so a reload does not lose the selection).
    /// </summary>
    private string CurrentFilterKey => Filters.FirstOrDefault(f => f.IsSelected)?.Key ?? AllKey;

    /// <summary>
    /// Projects an event onto a row: the pair gives the name, <see cref="JournalPhrases"/>
    /// renders the phrase, the outcome maps to a color token.
    /// </summary>
    private static LogRow Row(FolderPair pair, SyncEvent e) =>
        new(e.At.ToLocalTime().ToString("HH:mm"),
            string.IsNullOrWhiteSpace(pair.DisplayName) ? pair.RemoteRootPath : pair.DisplayName,
            JournalPhrases.Render(e),
            e.Outcome switch
            {
                SyncEventOutcome.Warning => "StatusWarning",
                SyncEventOutcome.Error => "StatusError",
                SyncEventOutcome.Success => "PrimaryText",
                _ => "TextSecondary",
            })
        { Conflict = ConflictEvents.IsConflictRow(e) };

    /// <summary>
    /// Rebuilds the list for a filter (freshest on top — _allRows is already time-ordered).
    /// </summary>
    private void ApplyFilter(string key)
    {
        var filtered = key switch
        {
            WarningKey => _allRows.Where(r => r.Conflict).ToList(), // the shared rule, not the color
            ErrorKey => _allRows.Where(r => r.ColorToken == "StatusError").ToList(),
            _ => _allRows.ToList(),
        };

        Items.Clear();
        foreach (var row in filtered)
        {
            Items.Add(row);
        }

        IsEmpty = Items.Count == 0;
    }
}
