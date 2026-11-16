using System.Globalization;

namespace FileFerry.App.Converters;

/// <summary>
/// String → "has text" for IsVisible (inline errors/hints): null/empty means hidden.
/// Singleton via <see cref="Instance"/> so XAML does not create a copy per binding.
/// </summary>
public sealed class HasTextConverter : IValueConverter
{
    /// <summary>
    /// Shared instance for XAML.
    /// </summary>
    public static readonly HasTextConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}
