using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BetterTranslator.Mac.App.Converters;

public sealed class BrushShimConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        System.Windows.Media.Brush shim => shim.Value,
        IBrush brush => brush,
        Color color => new SolidColorBrush(color),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
