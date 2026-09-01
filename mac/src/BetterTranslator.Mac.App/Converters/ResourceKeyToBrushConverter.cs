using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BetterTranslator.Mac.App.Converters;

public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Avalonia.Application.Current is null)
        {
            return null;
        }

        if (!Avalonia.Application.Current.TryFindResource(key, out var resource))
        {
            return null;
        }

        return resource switch
        {
            IBrush brush => brush,
            System.Windows.Media.Brush shim => shim.Value,
            _ => null,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
