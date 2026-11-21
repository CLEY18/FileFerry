using System.Globalization;
using FileFerry.App.Theming;

namespace FileFerry.App.Converters;

/// <summary>
/// Color-token name + theme stamp to a concrete <see cref="Color"/> from the application resources.
/// The token string names the color; the changing <see cref="ThemeState.Version"/> makes the
/// binding re-evaluate when the theme service re-maps tokens (a plain single-source binding would
/// resolve once and then hold a color of the previous palette). Singleton for XAML.
/// </summary>
public sealed class ThemeTokenConverter : IMultiValueConverter
{
    /// <summary>
    /// Shared instance for XAML.
    /// </summary>
    public static readonly ThemeTokenConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2
        && values[0] is string key
        && Application.Current is { } app
        && app.Resources.TryGetValue(key, out var resource)
        && resource is Color color
            ? color
            : Colors.Transparent;

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}
