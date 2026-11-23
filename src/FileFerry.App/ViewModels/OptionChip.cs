namespace FileFerry.App.ViewModels;

/// <summary>
/// Base of a segment option (theme mode, schedule, log filter): label, selected state, and
/// colors as token names. An unselected option carries no fill - the token converter resolves
/// an unknown name to transparent - because the shared outline, not a tint, holds the structure.
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

    /// <summary>
    /// The selected option is filled and wears the primary text tone; an unselected one is
    /// transparent and wears the primary ink of the surface it sits on.
    /// </summary>
    public string BackgroundToken => IsSelected ? "PrimaryContainer" : string.Empty;

    public string TextToken => IsSelected ? "PrimaryText" : "TextPrimary";

    /// <summary>
    /// Color of the selected-mark: the mark always occupies its place (a chip must not change
    /// width when the selection moves), so an unselected chip renders it from an unknown token —
    /// the converter resolves that to transparent, an invisible mark that still holds the width.
    /// A selected chip paints it in the chip's own text color.
    /// </summary>
    public string MarkToken => IsSelected ? TextToken : string.Empty;
}
