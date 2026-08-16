using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

/// <summary>
/// Two small things menus need that WPF does not carry: a button that opens the
/// menu of the row it sits in, and a menu item that reads as destructive.
/// </summary>
public static class Menus
{
    /// <summary>
    /// Makes a button open the context menu of the row it sits in, so the same
    /// menu is reachable by clicking and by right clicking without being
    /// declared twice. A ContextMenu already carries placement, dismissal and
    /// keyboard behaviour a hand rolled popup would have to repeat.
    /// </summary>
    public static readonly DependencyProperty OpensRowMenuProperty = DependencyProperty.RegisterAttached(
        "OpensRowMenu",
        typeof(bool),
        typeof(Menus),
        new PropertyMetadata(false, OnOpensRowMenuChanged));

    /// <summary>
    /// Marks the item whose action cannot be undone. Colour is not the only
    /// signal -- the action is named Delete and confirms in a dialog -- but it
    /// is the one that separates it from the items above at a glance.
    /// </summary>
    public static readonly DependencyProperty IsDangerProperty = DependencyProperty.RegisterAttached(
        "IsDanger",
        typeof(bool),
        typeof(Menus),
        new PropertyMetadata(false));

    public static void SetOpensRowMenu(DependencyObject element, bool value) =>
        element.SetValue(OpensRowMenuProperty, value);

    public static bool GetOpensRowMenu(DependencyObject element) =>
        (bool)element.GetValue(OpensRowMenuProperty);

    public static void SetIsDanger(DependencyObject element, bool value) =>
        element.SetValue(IsDangerProperty, value);

    public static bool GetIsDanger(DependencyObject element) =>
        (bool)element.GetValue(IsDangerProperty);

    private static void OnOpensRowMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnClick;

        if (e.NewValue is true)
        {
            button.Click += OnClick;
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        var button = (ButtonBase)sender;

        if (FindMenu(button) is not { } menu)
        {
            return;
        }

        // Under the button rather than at the pointer, so a menu opened by
        // clicking lines up with what opened it.
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;

        // The row beneath must not also treat this as a click on itself.
        e.Handled = true;
    }

    /// <summary>
    /// The button's own menu if it has one, otherwise the nearest one above it.
    /// </summary>
    private static ContextMenu? FindMenu(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement { ContextMenu: { } menu })
            {
                return menu;
            }

            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
