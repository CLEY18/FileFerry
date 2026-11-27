namespace FileFerry.App.ViewModels;

/// <summary>
/// Row of the global log global log: time · pair · event message.
/// "12↑ 3↓"-style direction counters need enriched engine events —
/// v1 shows the event text; color by outcome is never the only signal.
/// </summary>
/// <param name="Time">"HH:mm" of local time.</param>
/// <param name="PairName">Name of the pair the event came.</param>
/// <param name="Text">Message (no secrets — journal invariant).</param>
/// <param name="ColorToken">Color token by outcome.</param>
public sealed record LogRow(string Time, string PairName, string Text, string ColorToken)
{
    /// <summary>
    /// Whether the shared conflict rule recognizes this event as an open conflict row —
    /// the conflicts chip filters on the same rule the counters count, never on the color.
    /// </summary>
    public bool Conflict { get; init; }

    /// <summary>
    /// TalkBack description: the row is a single focus element.
    /// </summary>
    public string SemanticDescription => $"{Time}, {PairName}, {Text}";
}
