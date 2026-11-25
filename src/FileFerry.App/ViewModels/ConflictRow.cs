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
/// <param name="StatusText">State line: "both versions kept", the kept side after a resolution,
/// or the hand-resolve hint when the conflict involves a folder.</param>
/// <param name="KeepServerCommand">Fallback action: keep the server version.</param>
/// <param name="KeepLocalCommand">Fallback action: keep the local version.</param>
/// <param name="IsResolved">A later resolution acknowledged this conflict.</param>
/// <param name="NeedsHandResolve">A folder is involved: no copies exist, one side must be
/// renamed by hand — the actions could only fail, so the row shows the hint instead.</param>
public sealed record ConflictRow(
    RelativePath Path,
    string PathText,
    string WhenText,
    string Message,
    string StatusText,
    ICommand? KeepServerCommand,
    ICommand? KeepLocalCommand,
    bool IsResolved = false,
    bool NeedsHandResolve = false)
{
    /// <summary>
    /// Actions are offered only while the conflict is unacknowledged and actually resolvable.
    /// </summary>
    public bool ShowActions => !IsResolved && !NeedsHandResolve;

    /// <summary>
    /// TalkBack description: the row is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{PathText}. {Message}. {StatusText}";
}
