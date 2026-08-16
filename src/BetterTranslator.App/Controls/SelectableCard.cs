using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

/// <summary>
/// Makes a whole card toggle the checkbox inside it, so picking a source means
/// hitting the card rather than the small box in its corner.
///
/// The checkbox stays where it is and keeps its own focus and space bar, so the
/// keyboard route is untouched. A click that a button inside the card has
/// already dealt with -- Choose, Pick -- is left alone: those do their own
/// thing and must not also flip the selection.
/// </summary>
public static class SelectableCard
{
    public static readonly DependencyProperty TogglesSelectionProperty = DependencyProperty.RegisterAttached(
        "TogglesSelection",
        typeof(bool),
        typeof(SelectableCard),
        new PropertyMetadata(false, OnTogglesSelectionChanged));

    public static void SetTogglesSelection(DependencyObject element, bool value) =>
        element.SetValue(TogglesSelectionProperty, value);

    public static bool GetTogglesSelection(DependencyObject element) =>
        (bool)element.GetValue(TogglesSelectionProperty);

    private static void OnTogglesSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement card)
        {
            return;
        }

        card.PreviewMouseLeftButtonUp -= OnCardClick;

        if (e.NewValue is true)
        {
            card.PreviewMouseLeftButtonUp += OnCardClick;
        }
    }

    private static void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        // A press that landed on a control of its own is that control's to
        // answer, not the card's.
        if (IsOwnedByAControl(e.OriginalSource as DependencyObject, (DependencyObject)sender))
        {
            return;
        }

        if (FindCheckBox((DependencyObject)sender) is { IsEnabled: true } box)
        {
            box.IsChecked = box.IsChecked != true;
            e.Handled = true;
        }
    }

    private static bool IsOwnedByAControl(DependencyObject? node, DependencyObject card)
    {
        while (node is not null && !ReferenceEquals(node, card))
        {
            if (node is ButtonBase or TextBoxBase)
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private static CheckBox? FindCheckBox(DependencyObject node)
    {
        if (node is CheckBox box)
        {
            return box;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindCheckBox(VisualTreeHelper.GetChild(node, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
