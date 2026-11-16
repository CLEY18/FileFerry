using System.Windows.Input;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Row of the trash buffer screen: a deletion copy with TTL progress ("expires in 21 days")
/// and a "Restore" action; the row carries its own command (MAUI 10 template limitation).
/// </summary>
/// <param name="OriginText">Captured path (the card's primary line).</param>
/// <param name="DeletedText">"Deleted N days ago" (relative time).</param>
/// <param name="ExpiresText">"Expires in …" — the remaining TTL.</param>
/// <param name="Progress">Elapsed TTL fraction (0..1) for the bar.</param>
/// <param name="RestoreCommand">"Restore" action (a file operation — requires the engine).</param>
public sealed record StashRow(
    string OriginText,
    string DeletedText,
    string ExpiresText,
    double Progress,
    ICommand? RestoreCommand)
{
    /// <summary>
    /// Path of the copy in the stash (payload for the "Restore" action; empty — nothing to restore).
    /// </summary>
    public string StashPath { get; init; } = string.Empty;

    /// <summary>
    /// Canonical path of the original (where to copy back on restore).
    /// </summary>
    public string OriginPath { get; init; } = string.Empty;

    /// <summary>
    /// TalkBack description: the card is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{OriginText}. {DeletedText}. {ExpiresText}";
}
