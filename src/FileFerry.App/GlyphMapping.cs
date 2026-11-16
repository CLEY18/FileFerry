namespace FileFerry.App;

/// <summary>
/// Material Symbols Outlined glyph table (/): one table, codepoint
/// encoding (no ligatures). The font is a static subset 
/// google/material-design-icons/variablefont (Apache 2.0; LICENSE ships next to
/// the file) with axes frozen at wght 400 / opsz 24,
/// Resources/Fonts/MaterialSymbolsOutlined.ttf.
/// Note: in the current font the `restore_trash` glyph
/// is actually named `restore_from_trash`.
/// </summary>
public static class GlyphMapping
{
    /// <summary>
    /// Font alias registered in <see cref="MauiProgram"/>.
    /// </summary>
    public const string FontFamily = "MaterialSymbols";

    /// <summary>
    /// Remote / local folder.
    /// </summary>
    public const string Folder = "\uE2C7";

    /// <summary>
    /// File.
    /// </summary>
    public const string Description = "\uE873";

    /// <summary>
    /// Synchronization (spins during a run).
    /// </summary>
    public const string Sync = "\uE627";

    /// <summary>
    /// Success.
    /// </summary>
    public const string CheckCircle = "\uF0BE";

    /// <summary>
    /// Conflict (merge).
    /// </summary>
    public const string Merge = "\uEB98";

    public const string Error = "\uF8B6";

    /// <summary>
    /// Offline / server unavailable.
    /// </summary>
    public const string CloudOff = "\uE2C1";

    /// <summary>
    /// Pair paused.
    /// </summary>
    public const string PauseCircle = "\uE1A2";

    /// <summary>
    /// Add a pair (FAB).
    /// </summary>
    public const string Add = "\uE145";

    /// <summary>
    /// Deletion stash; still lists the old name restore_trash.
    /// </summary>
    public const string RestoreFromTrash = "\uE938";

    public const string Settings = "\uE8B8";

    /// <summary>
    /// Event log.
    /// </summary>
    public const string Article = "\uEF42";

    /// <summary>
    /// Row action (context menu).
    /// </summary>
    public const string MoreVert = "\uE5D4";

    /// <summary>
    /// Server / account.
    /// </summary>
    public const string Dns = "\uE875";

    /// <summary>
    /// Theme.
    /// </summary>
    public const string DarkMode = "\uE51C";

    /// <summary>
    /// Drill-down navigation (RemoteBrowserRow).
    /// </summary>
    public const string ChevronRight = "\uE5CC";
}
