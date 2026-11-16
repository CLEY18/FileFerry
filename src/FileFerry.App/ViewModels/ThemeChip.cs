using FileFerry.App.Theming;

namespace FileFerry.App.ViewModels;

/// <summary>
/// Settings screen theme segment chip: a <see cref="ThemeMode"/> palette (NC/Dynamic).
/// </summary>
public sealed class ThemeChip : OptionChip
{
    public ThemeChip(ThemeMode value, string label)
        : base(label) => Value = value;

    public ThemeMode Value { get; }
}
