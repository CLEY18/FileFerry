namespace FileFerry.App.ViewModels;

/// <summary>
/// Global log filter chip: event filter key ("all" / "warning" / "error").
/// </summary>
public sealed class LogFilterChip : OptionChip
{
    public LogFilterChip(string key, string label)
        : base(label) => Key = key;

    /// <summary>
    /// Filter key (matched against the event outcome).
    /// </summary>
    public string Key { get; }
}
