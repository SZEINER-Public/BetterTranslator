using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BetterTranslator.App.Converters;

/// <summary>
/// The counterpart to the framework's BooleanToVisibilityConverter, for the
/// panels that show when a flag is false.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
