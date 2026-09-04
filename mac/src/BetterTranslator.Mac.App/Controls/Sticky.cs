using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace BetterTranslator.Mac.App.Controls;

public static class Sticky
{
    public static readonly AttachedProperty<bool> WithinRegionProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("WithinRegion", typeof(Sticky));

    private static readonly AttachedProperty<ScrollViewer?> ScrollProperty =
        AvaloniaProperty.RegisterAttached<Control, ScrollViewer?>("Scroll", typeof(Sticky));

    static Sticky() =>
        WithinRegionProperty.Changed.AddClassHandler<Control>(OnWithinRegionChanged);

    public static void SetWithinRegion(Control element, bool value) =>
        element.SetValue(WithinRegionProperty, value);

    public static bool GetWithinRegion(Control element) =>
        element.GetValue(WithinRegionProperty);

    private static void OnWithinRegionChanged(Control element, AvaloniaPropertyChangedEventArgs e)
    {
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;

        if (e.NewValue is true)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;

            if (element.IsLoaded)
            {
                Attach(element);
            }
        }
        else
        {
            Detach(element);
        }
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e) => Attach((Control)sender!);

    private static void OnUnloaded(object? sender, RoutedEventArgs e) => Detach((Control)sender!);

    private static void Attach(Control element)
    {
        if (element.FindAncestorOfType<ScrollViewer>() is not { } scroll)
        {
            return;
        }

        element.SetValue(ScrollProperty, scroll);
        scroll.ScrollChanged += element.OnScrolled;
        Follow(element);
    }

    private static void Detach(Control element)
    {
        if (element.GetValue(ScrollProperty) is { } scroll)
        {
            scroll.ScrollChanged -= element.OnScrolled;
            element.ClearValue(ScrollProperty);
        }

        if (element.RenderTransform is TranslateTransform slide)
        {
            slide.Y = 0;
        }
    }

    private static void OnScrolled(this Control element, object? sender, ScrollChangedEventArgs e) =>
        Follow(element);

    private static void Follow(Control element)
    {
        if (element.GetValue(ScrollProperty) is not { } scroll
            || element.GetVisualParent() is not Control region
            || !region.IsEffectivelyVisible
            || region.Bounds.Height <= 0)
        {
            return;
        }

        if (element.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            element.RenderTransform = slide;
        }

        if (region.TranslatePoint(default, scroll) is not { } origin)
        {
            return;
        }

        var travel = region.Bounds.Height - element.Bounds.Height;

        slide.Y = origin.Y < 0 ? Math.Clamp(-origin.Y, 0, Math.Max(travel, 0)) : 0;
    }
}
