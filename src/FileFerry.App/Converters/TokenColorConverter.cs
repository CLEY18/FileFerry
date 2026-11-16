using System.Globalization;

namespace FileFerry.App.Converters;

/// <summary>
/// Color-token name (a <c>Colors.xaml</c> key) → <see cref="Color"/> from application
/// resources: XAML carries only token names, never hex; <c>ThemeService</c> resolves
/// the values (theme + mode). Singleton for XAML so bindings do not spawn instances.
/// </summary>
public sealed class TokenColorConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML.
    /// </summary>
    public static readonly TokenColorConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key
        && Application.Current is { } app
        && app.Resources.TryGetValue(key, out var resource)
        && resource is Color color
            ? color
            : Colors.Transparent;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}
