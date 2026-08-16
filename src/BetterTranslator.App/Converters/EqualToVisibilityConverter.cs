using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BetterTranslator.App.Converters;

/// <summary>
/// Visible when two bound values are the same, for a row that has to mark itself
/// as the current one.
///
/// A converter rather than an IsCurrent flag on the row: the language list is
/// rebuilt whenever the model changes, and a flag would have to be recomputed
/// across every row on every selection. Comparing at render time cannot fall out
/// of step with what the composer is actually set to.
/// </summary>
public sealed class EqualToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return Visibility.Collapsed;
        }

        var same = values[0] is string a && values[1] is string b
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : Equals(values[0], values[1]);

        return same ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way: a tick is a readout of the selection, never a way to set it.");
}
