using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace BetterTranslator.Mac.App.Controls;

public static class SelectableCard
{
    public static readonly AttachedProperty<bool> TogglesSelectionProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("TogglesSelection", typeof(SelectableCard));

    static SelectableCard()
    {
        TogglesSelectionProperty.Changed.AddClassHandler<Control, bool>(OnTogglesSelectionChanged);
    }

    public static void SetTogglesSelection(Control element, bool value) =>
        element.SetValue(TogglesSelectionProperty, value);

    public static bool GetTogglesSelection(Control element) =>
        element.GetValue(TogglesSelectionProperty);

    private static void OnTogglesSelectionChanged(Control card, AvaloniaPropertyChangedEventArgs<bool> e)
    {
        card.RemoveHandler(InputElement.PointerReleasedEvent, OnCardClick);

        if (e.GetNewValue<bool>())
        {
            card.AddHandler(InputElement.PointerReleasedEvent, OnCardClick, RoutingStrategies.Tunnel);
        }
    }

    private static void OnCardClick(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control card || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (IsOwnedByAControl(e.Source as AvaloniaObject, card))
        {
            return;
        }

        if (FindCheckBox(card) is { IsEnabled: true } box)
        {
            box.IsChecked = box.IsChecked != true;
            e.Handled = true;
        }
    }

    private static bool IsOwnedByAControl(AvaloniaObject? node, AvaloniaObject card)
    {
        while (node is not null && !ReferenceEquals(node, card))
        {
            if (node is Button or TextBox)
            {
                return true;
            }

            node = (node as Visual)?.GetVisualParent() ?? (node as ILogical)?.LogicalParent as AvaloniaObject;
        }

        return false;
    }

    private static CheckBox? FindCheckBox(Visual node)
    {
        if (node is CheckBox box)
        {
            return box;
        }

        foreach (var child in node.GetVisualChildren())
        {
            if (FindCheckBox(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
