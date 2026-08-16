using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BetterTranslator.App.Controls;

namespace BetterTranslator.App.Converters;

/// <summary>
/// Resolves a resource key held in data to the icon it names. A dialog says
/// which glyph its actions carry without the view model holding a WPF type, the
/// same way a language carries a flag key rather than a Brush.
/// </summary>
public sealed class ResourceKeyToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as IconDefinition : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
