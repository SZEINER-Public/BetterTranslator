using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using BetterTranslator.App.Controls;

namespace BetterTranslator.Mac.App.Converters;

public sealed class ResourceKeyToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Avalonia.Application.Current is null)
        {
            return null;
        }

        return Avalonia.Application.Current.TryFindResource(key, out var resource)
            ? resource as IconDefinition
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
