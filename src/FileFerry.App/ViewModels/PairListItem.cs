using System.Windows.Input;
using FileFerry.App.Resources;
using FileFerry.Core.Domain;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Item of the pair list: prepared PairCard data + the VM's commands nested into the
/// card — XAML templates cannot resolve ancestor bindings in MAUI 10, so every card carries
/// its own <see cref="ICommand"/>. No translation, no formatting inside XAML.
/// </summary>
/// <param name="Id">Pair id (for navigation/actions).</param>
/// <param name="RemotePath">Remote path (a Heading-like line).</param>
/// <param name="LocalSummary">Local folder for the hint line.</param>
/// <param name="Status">Chip status.</param>
/// <param name="StatusText">Localized chip text.</param>
/// <param name="IsPaused">Paused — drives the "active on top" sorting and the "Resume" action.</param>
/// <param name="IsSyncing">Show the progress bar (only while a run is active).</param>
/// <param name="Progress">Fraction 0..1 for the bar.</param>
/// <param name="ProgressText">"File X of Y" / "Preparing…" line under the bar.</param>
public sealed record PairListItem(
    Guid Id,
    string RemotePath,
    string LocalSummary,
    PairStatus Status,
    string StatusText,
    bool IsPaused,
    bool IsSyncing,
    double Progress,
    string ProgressText = "")
{
    /// <summary>
    /// Card tap → the pair details screen.
    /// </summary>
    public ICommand? TapCommand { get; init; }

    /// <summary>
    /// ⋮ / long-press → pair actions.
    /// </summary>
    public ICommand? MenuCommand { get; init; }

    /// <summary>
    /// Menu action: pause/resume (this very object is the parameter).
    /// </summary>
    public ICommand? PauseCommand { get; init; }

    /// <summary>
    /// Menu action: sync now.
    /// </summary>
    public ICommand? SyncNowCommand { get; init; }

    /// <summary>
    /// Menu action: delete the pair config (with confirmation).
    /// </summary>
    public ICommand? DeleteCommand { get; init; }

    /// <summary>
    /// Chip microcopy by status (from the string resources).
    /// </summary>
    public static string StatusTextFor(PairStatus status) => status switch
    {
        PairStatus.Syncing => AppStrings.StatusSyncing,
        PairStatus.Success => AppStrings.StatusSuccess,
        PairStatus.Conflict => AppStrings.StatusConflict,
        PairStatus.Error => AppStrings.StatusErrorChip,
        PairStatus.Offline => AppStrings.StatusOffline,
        _ => AppStrings.StatusIdle,
    };

    /// <summary>
    /// Projects the domain pair onto a card; has no "Paused" state — a paused pair shows Idle.
    /// <paramref name="status"/> is the summary of the last completed run (the card once showed
    /// "Waiting" forever because the status was never projected); the Conflict chip carries the count.
    /// </summary>
    public static PairListItem From(FolderPair pair, PairStatus status = PairStatus.Idle, int conflictCount = 0)
    {
        var chip = pair.Paused ? PairStatus.Idle : status;

        var localName = FolderPair.LocalFolderName(pair.LocalTreeKey);
        var display = localName.Length > 0 ? localName : pair.DisplayName;
        return new PairListItem(
            pair.Id,
            pair.RemoteRootPath,
            $"{AppStrings.PairCardLocalPrefix} › {display}", // "Device files › Sync/Projects"
            chip,
            chip == PairStatus.Conflict && conflictCount > 0 ? ConflictChipText(conflictCount) : StatusTextFor(chip),
            pair.Paused,
            IsSyncing: false,
            Progress: 0);
    }

    /// <summary>
    /// Conflict chip with a count ("3 conflicts"); pluralization.
    /// </summary>
    public static string ConflictChipText(int count) =>
        $"{count} {Services.Humanize.Count(count, AppStrings.ChipConflictOne, AppStrings.ChipConflictFew, AppStrings.ChipConflictMany)}";

    /// <summary>
    /// Expanded TalkBack description: the card is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{RemotePath}. {LocalSummary}. {StatusText}";

    /// <summary>
    /// "Pause/resume" action label for the card's context menu.
    /// </summary>
    public string MenuPauseLabel => IsPaused ? AppStrings.CardActionResume : AppStrings.CardActionPause;
}
