using System.Globalization;
using Avalonia.Data.Converters;

namespace BetterTranslator.Mac.App.Converters;

public sealed class EqualToVisibilityConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
        {
            return false;
        }

        return values[0] is string a && values[1] is string b
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : Equals(values[0], values[1]);
    }
}
