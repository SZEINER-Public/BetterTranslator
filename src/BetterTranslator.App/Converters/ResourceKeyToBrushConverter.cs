using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BetterTranslator.App.Converters;

/// <summary>
/// Resolves a resource key held in data to the brush it names. Languages carry
/// a flag key rather than a Brush so the view model stays free of WPF types.
/// </summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as Brush : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
