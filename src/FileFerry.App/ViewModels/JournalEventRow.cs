using FileFerry.App.Services;
using FileFerry.Core.Domain;

namespace FileFerry.App.ViewModels;

/// <summary>
/// "Pair journal" row on the pair details: ready text + a color token by event outcome
/// (color is never the only signal — the timestamp and message text sit next to it).
/// </summary>
/// <param name="Time">"HH:mm" of the event's local time.</param>
/// <param name="Text">Event message.</param>
/// <param name="ColorToken">Color token name (a Colors.xaml key) for the text.</param>
public sealed record JournalEventRow(string Time, string Text, string ColorToken)
{
    /// <summary>
    /// Projection from a domain event (phrase rendered in the UI language);
    /// Warning/Error map to the status tokens.
    /// </summary>
    public static JournalEventRow From(SyncEvent e) =>
        new(e.At.ToLocalTime().ToString("HH:mm"),
            JournalPhrases.Render(e),
            e.Outcome switch
            {
                SyncEventOutcome.Warning => "StatusWarning",
                SyncEventOutcome.Error => "StatusError",
                _ => "TextSecondary",
            });
}
