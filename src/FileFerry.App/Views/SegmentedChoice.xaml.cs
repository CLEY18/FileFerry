using System.Collections;

namespace FileFerry.App.Views;

/// <summary>
/// Code-behind of the segmented choice XAML: forwards the item source (the option chips of a
/// segment) to the layout inside the shared outline.
/// </summary>
public partial class SegmentedChoice : Border
{
    /// <summary>
    /// The option items of the segment (an <see cref="IEnumerable"/> of option chips).
    /// </summary>
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SegmentedChoice),
        propertyChanged: (b, _, n) => BindableLayout.SetItemsSource(
            ((SegmentedChoice)b).SegmentRow, n is IEnumerable source ? source : Array.Empty<object>()));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public SegmentedChoice()
    {
        InitializeComponent();
    }
}
