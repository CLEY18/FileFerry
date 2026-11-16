using FileFerry.App.Resources;
using FileFerry.Core.Domain;
using FileFerry.Core.Sync;

namespace FileFerry.App.Services;

/// <summary>
/// The journal renders in the interface language — the database stores the event's intent
/// (<see cref="EventKind"/> + arguments) and the text is taken from <see cref="AppStrings"/> at
/// display time. Switching the system language repaints the history too. An unknown key (a new
/// kind on older resources) shows its own name — noticeable, but not a crash.
/// </summary>
public static class JournalPhrases
{
    /// <summary>
    /// Event text in the current language + the tangent path suffix (fact · object).
    /// </summary>
    public static string Render(SyncEvent e)
    {
        var (key, args) = e.Phrase();
        var format = AppStrings.GetByKey(key, key);
        var text = args.Length == 0 ? format : string.Format(format, args);
        return e.Path is { IsEmpty: false } path ? $"{text} · {path}" : text;
    }
}
