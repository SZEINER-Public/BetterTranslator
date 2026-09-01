using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace BetterTranslator.Mac.App.Controls;

public static class Menus
{
    public static readonly AttachedProperty<bool> OpensRowMenuProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OpensRowMenu", typeof(Menus));

    public static readonly AttachedProperty<bool> IsDangerProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsDanger", typeof(Menus));

    static Menus()
    {
        OpensRowMenuProperty.Changed.AddClassHandler<Button, bool>(OnOpensRowMenuChanged);
        IsDangerProperty.Changed.AddClassHandler<Control, bool>(OnIsDangerChanged);
    }

    public static void SetOpensRowMenu(Control element, bool value) =>
        element.SetValue(OpensRowMenuProperty, value);

    public static bool GetOpensRowMenu(Control element) =>
        element.GetValue(OpensRowMenuProperty);

    public static void SetIsDanger(Control element, bool value) =>
        element.SetValue(IsDangerProperty, value);

    public static bool GetIsDanger(Control element) =>
        element.GetValue(IsDangerProperty);

    private static void OnOpensRowMenuChanged(Button button, AvaloniaPropertyChangedEventArgs<bool> e)
    {
        button.Click -= OnClick;

        if (e.GetNewValue<bool>())
        {
            button.Click += OnClick;
        }
    }

    private static void OnIsDangerChanged(Control element, AvaloniaPropertyChangedEventArgs<bool> e) =>
        ((IPseudoClasses)element.Classes).Set(":danger", e.GetNewValue<bool>());

    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || FindMenu(button) is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.Open(button);

        e.Handled = true;
    }

    private static ContextMenu? FindMenu(AvaloniaObject? node)
    {
        while (node is not null)
        {
            if (node is Control { ContextMenu: { } menu })
            {
                return menu;
            }

            node = (node as Visual)?.GetVisualParent() ?? (node as ILogical)?.LogicalParent as AvaloniaObject;
        }

        return null;
    }
}
