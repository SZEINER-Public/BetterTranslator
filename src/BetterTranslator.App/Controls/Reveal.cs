using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.Controls;

/// <summary>
/// The swap from placeholder to answer. The element fades up on the enter curve
/// when it first carries the translation, and says so once to a screen reader.
///
/// Attached rather than written into the view, because the element it applies to
/// lives in a DataTemplate: there is one per entry and none of them can be
/// reached by name from code-behind.
/// </summary>
public static class Reveal
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target",
        typeof(bool),
        typeof(Reveal),
        new PropertyMetadata(false, OnTargetChanged));

    public static void SetTarget(DependencyObject element, bool value) => element.SetValue(TargetProperty, value);

    public static bool GetTarget(DependencyObject element) => (bool)element.GetValue(TargetProperty);

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnVisibleChanged;

        if (e.NewValue is true)
        {
            element.IsVisibleChanged += OnVisibleChanged;
        }
    }

    /// <summary>
    /// Becoming visible is the arrival: the region renders the translation only
    /// once there is one, so this fires when the answer lands and not before.
    /// </summary>
    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || e.NewValue is not true)
        {
            return;
        }

        // Never from zero. The first frame has to be legible whether the fade
        // runs, is cut short, or never runs at all -- see the motion digest's
        // first-frame rule. Set before the animation, because it is the base
        // value the animation interpolates from.
        element.Opacity = Tokens.Number("RevealFromOpacity");

        MotionService.AnimateDouble(
            element,
            UIElement.OpacityProperty,
            1,
            Tokens.Get<Duration>("Motion200"),
            Tokens.Get<KeySpline>("EaseStandard"));

        Announce(element);
    }

    /// <summary>
    /// One announcement per answer. The element carries the polite live setting
    /// in markup, but WPF raises nothing for it on its own, so the event is
    /// raised here -- when the translation arrives, rather than on every change
    /// to the text.
    /// </summary>
    private static void Announce(UIElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(element)
            ?? UIElementAutomationPeer.CreatePeerForElement(element);

        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
