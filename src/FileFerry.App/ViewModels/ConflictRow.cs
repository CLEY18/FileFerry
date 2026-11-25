using System.Windows.Input;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Row of the conflicts screen: a conflicting event with KeepBoth already applied
/// ("keep both" — done, shown as state) plus the actions it offers itself (MAUI 10 templates
/// do not resolve ancestor bindings). A file row carries the two keep-side actions; a folder
/// row carries its four folder actions (rename or delete either side — there are no copies
/// to keep). After a resolution the row turns into its result status and the actions hide.
/// </summary>
/// <param name="Path">Conflicting path in pair coordinates (the resolution argument).</param>
/// <param name="PathText">Path of the conflicting entry (relative to the pair root).</param>
/// <param name="WhenText">Relative event time ("5 min ago").</param>
/// <param name="Message">Message from the run journal.</param>
/// <param name="StatusText">State line: "both versions kept", or the kept side after a
/// resolution; empty while a folder row awaits its action (the actions are the guidance).</param>
/// <param name="KeepServerCommand">File action: keep the server version.</param>
/// <param name="KeepLocalCommand">File action: keep the local version.</param>
/// <param name="IsResolved">A later resolution acknowledged this conflict.</param>
/// <param name="IsFolder">A folder is involved: no copies exist, the row offers folder actions.</param>
/// <param name="FolderRenameServerCommand">Folder action: MOVE the server folder under a conflict name.</param>
/// <param name="FolderRenameLocalCommand">Folder action: MOVE the device folder under a conflict name.</param>
/// <param name="FolderDeleteServerCommand">Folder action: buffer every server file, delete the server folder.</param>
/// <param name="FolderDeleteLocalCommand">Folder action: buffer every device file, delete the device folder.</param>
public sealed record ConflictRow(
    RelativePath Path,
    string PathText,
    string WhenText,
    string Message,
    string StatusText,
    ICommand? KeepServerCommand,
    ICommand? KeepLocalCommand,
    bool IsResolved = false,
    bool IsFolder = false,
    ICommand? FolderRenameServerCommand = null,
    ICommand? FolderRenameLocalCommand = null,
    ICommand? FolderDeleteServerCommand = null,
    ICommand? FolderDeleteLocalCommand = null)
{
    /// <summary>
    /// Keep-side actions are offered only on an unacknowledged file row.
    /// </summary>
    public bool ShowActions => !IsResolved && !IsFolder;

    /// <summary>
    /// The four folder actions on an unacknowledged folder row.
    /// </summary>
    public bool ShowFolderActions => !IsResolved && IsFolder;

    /// <summary>
    /// A folder row awaiting its action has no state line yet — the actions say what comes next.
    /// </summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>
    /// TalkBack description: the row is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{PathText}. {Message}. {StatusText}";
}
