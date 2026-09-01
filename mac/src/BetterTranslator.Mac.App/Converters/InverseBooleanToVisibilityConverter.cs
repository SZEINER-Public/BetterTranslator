using System.Globalization;
using Avalonia.Data.Converters;

namespace BetterTranslator.Mac.App.Converters;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false;
}
