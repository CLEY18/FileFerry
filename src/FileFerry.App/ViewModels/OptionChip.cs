namespace FileFerry.App.ViewModels;

/// <summary>
/// Base of a segment chip (segments/chips): label, selected state, and colors
/// as token names. The concrete value type lives in the derived chips.
/// </summary>
public abstract class OptionChip : Mvvm.ObservableObject
{
    private bool _isSelected;

    protected OptionChip(string label) => Label = label;

    /// <summary>
    /// Chip label (microcopy).
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Command selecting this very chip (the VM assigns it after construction).
    /// </summary>
    public System.Windows.Input.ICommand? SelectCommand { get; set; }

    /// <summary>
    /// Whether the chip is selected (a segment has exactly one selected).
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
            {
                Raise(nameof(BackgroundToken));
                Raise(nameof(TextToken));
            }
        }
    }

    public string BackgroundToken => IsSelected ? "PrimaryContainer" : "SurfaceVariant";

    public string TextToken => IsSelected ? "PrimaryText" : "TextSecondary";
}
