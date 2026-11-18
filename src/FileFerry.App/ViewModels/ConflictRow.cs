using System.Windows.Input;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Row of the conflicts screen: a conflicting event with KeepBoth already applied
/// ("keep both" — done, shown as state) plus the two fallback actions it carries itself
/// (MAUI 10 templates do not resolve ancestor bindings). After a keep-side resolution the row
/// turns into its "kept …" status and the actions hide (acknowledged).
/// </summary>
/// <param name="Path">Conflicting file in pair coordinates (the resolution argument).</param>
/// <param name="PathText">Path of the conflicting file (relative to the pair root).</param>
/// <param name="WhenText">Relative event time ("5 min ago").</param>
/// <param name="Message">Message from the run journal.</param>
/// <param name="KeptBothText">State label: "both versions kept", or the kept side after a resolution.</param>
/// <param name="KeepServerCommand">Fallback action: keep the server version.</param>
/// <param name="KeepLocalCommand">Fallback action: keep the local version.</param>
/// <param name="IsResolved">A later resolution acknowledged this conflict.</param>
public sealed record ConflictRow(
    RelativePath Path,
    string PathText,
    string WhenText,
    string Message,
    string KeptBothText,
    ICommand? KeepServerCommand,
    ICommand? KeepLocalCommand,
    bool IsResolved = false)
{
    /// <summary>
    /// Actions are offered only while the conflict is unacknowledged.
    /// </summary>
    public bool ShowActions => !IsResolved;

    /// <summary>
    /// TalkBack description: the row is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{PathText}. {Message}. {KeptBothText}";
}
