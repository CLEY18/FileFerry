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
                Raise(nameof(MarkToken));
            }
        }
    }

    public string BackgroundToken => IsSelected ? "PrimaryContainer" : "SurfaceVariant";

    public string TextToken => IsSelected ? "PrimaryText" : "TextSecondary";

    /// <summary>
    /// Color of the selected-mark: the mark always occupies its place (a chip must not change
    /// width when the selection moves), so an unselected chip renders it from an unknown token —
    /// the converter resolves that to transparent, an invisible mark that still holds the width.
    /// A selected chip paints it in the chip's own text color.
    /// </summary>
    public string MarkToken => IsSelected ? TextToken : string.Empty;
}
