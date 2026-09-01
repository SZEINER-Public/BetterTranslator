using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace BetterTranslator.Mac.App.Controls;

public static class WheelChaining
{
    public static readonly AttachedProperty<bool> ChainsProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Chains", typeof(WheelChaining));

    static WheelChaining() =>
        ChainsProperty.Changed.AddClassHandler<ScrollViewer>(OnChainsChanged);

    public static void SetChains(ScrollViewer element, bool value) => element.SetValue(ChainsProperty, value);

    public static bool GetChains(ScrollViewer element) => element.GetValue(ChainsProperty);

    public static bool PassesOutward(double scrollableHeight, double verticalOffset, int delta)
    {
        if (scrollableHeight <= 0)
        {
            return true;
        }

        return delta < 0
            ? verticalOffset >= scrollableHeight
            : verticalOffset <= 0;
    }

    private static void OnChainsChanged(ScrollViewer scroll, AvaloniaPropertyChangedEventArgs e)
    {
        scroll.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);

        if (e.NewValue is true)
        {
            scroll.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnWheel,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
        }
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.Handled
            || sender is not ScrollViewer scroll
            || !PassesOutward(
                Math.Max(scroll.Extent.Height - scroll.Viewport.Height, 0),
                scroll.Offset.Y,
                Math.Sign(e.Delta.Y)))
        {
            return;
        }

        e.Handled = false;
    }
}
