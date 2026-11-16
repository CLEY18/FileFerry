using System.Windows.Input;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Row of the conflicts screen: a conflicting event with KeepBoth already applied
/// ("keep both" — done, shown as state) plus the two fallback actions it carries itself
/// (MAUI 10 templates do not resolve ancestor bindings).
/// </summary>
/// <param name="PathText">Path of the conflicting file (relative to the pair root).</param>
/// <param name="WhenText">Relative event time ("5 min ago").</param>
/// <param name="Message">Message from the run journal.</param>
/// <param name="KeptBothText">"both versions kept" state label (the action is already applied — shows a fact).</param>
/// <param name="KeepServerCommand">Fallback action: keep the server version (requires the engine).</param>
/// <param name="KeepLocalCommand">Fallback action: keep the local version (requires the engine).</param>
public sealed record ConflictRow(
    string PathText,
    string WhenText,
    string Message,
    string KeptBothText,
    ICommand? KeepServerCommand,
    ICommand? KeepLocalCommand)
{
    /// <summary>
    /// TalkBack description: the row is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{PathText}. {Message}. {KeptBothText}";
}
