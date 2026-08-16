using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

/// <summary>
/// Hands the wheel to the scroller outside when the one inside cannot use it.
///
/// A WPF ScrollViewer marks a wheel event handled whether or not it had anywhere
/// to scroll, so a small scroller sitting inside a large one swallows the
/// gesture: the pointer over a file preview stopped the history moving at all,
/// and an expanded preview with its own scrolling switched off stopped it while
/// having nothing of its own to scroll.
///
/// The rule is the one a reader expects without being told. The inner scroller
/// takes the wheel while it can still move in that direction, and the moment it
/// cannot, the wheel belongs to whatever contains it.
/// </summary>
public static class WheelChaining
{
    public static readonly DependencyProperty ChainsProperty = DependencyProperty.RegisterAttached(
        "Chains",
        typeof(bool),
        typeof(WheelChaining),
        new PropertyMetadata(false, OnChainsChanged));

    public static void SetChains(DependencyObject element, bool value) => element.SetValue(ChainsProperty, value);

    public static bool GetChains(DependencyObject element) => (bool)element.GetValue(ChainsProperty);

    /// <summary>
    /// Whether the wheel should pass outward, given what the inner scroller can
    /// still do. Written as a function of numbers so the rule can be tested
    /// without a window: routing is the part that needs one, the decision is not.
    /// </summary>
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

    private static void OnChainsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scroll)
        {
            return;
        }

        scroll.PreviewMouseWheel -= OnWheel;

        if (e.NewValue is true)
        {
            scroll.PreviewMouseWheel += OnWheel;
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled
            || sender is not ScrollViewer scroll
            || !PassesOutward(scroll.ScrollableHeight, scroll.VerticalOffset, e.Delta))
        {
            return;
        }

        if (VisualTreeHelper.GetParent(scroll) is not UIElement parent)
        {
            return;
        }

        // Marked before the new one is raised, so the inner scroller does not
        // also act on the gesture it just gave away.
        e.Handled = true;

        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = scroll,
        });
    }
}
