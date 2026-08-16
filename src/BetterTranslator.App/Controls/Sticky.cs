using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

public static class Sticky
{
    public static readonly DependencyProperty WithinRegionProperty = DependencyProperty.RegisterAttached(
        "WithinRegion",
        typeof(bool),
        typeof(Sticky),
        new PropertyMetadata(false, OnWithinRegionChanged));

    public static void SetWithinRegion(DependencyObject element, bool value) =>
        element.SetValue(WithinRegionProperty, value);

    public static bool GetWithinRegion(DependencyObject element) =>
        (bool)element.GetValue(WithinRegionProperty);

    private static void OnWithinRegionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

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

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((FrameworkElement)sender);

    private static readonly DependencyProperty ScrollProperty = DependencyProperty.RegisterAttached(
        "Scroll",
        typeof(ScrollViewer),
        typeof(Sticky),
        new PropertyMetadata(null));

    private static void Attach(FrameworkElement element)
    {
        if (Ancestor<ScrollViewer>(element) is not { } scroll)
        {
            return;
        }

        element.SetValue(ScrollProperty, scroll);
        scroll.ScrollChanged += element.OnScrolled;
        Follow(element);
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.GetValue(ScrollProperty) is ScrollViewer scroll)
        {
            scroll.ScrollChanged -= element.OnScrolled;
            element.ClearValue(ScrollProperty);
        }

        if (element.RenderTransform is TranslateTransform slide)
        {
            slide.Y = 0;
        }
    }

    private static void OnScrolled(this FrameworkElement element, object sender, ScrollChangedEventArgs e) =>
        Follow(element);

    private static void Follow(FrameworkElement element)
    {
        if (element.GetValue(ScrollProperty) is not ScrollViewer scroll
            || VisualTreeHelper.GetParent(element) is not FrameworkElement region
            || !region.IsVisible
            || region.ActualHeight <= 0)
        {
            return;
        }

        if (element.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            element.RenderTransform = slide;
        }

        double top;

        try
        {
            top = region.TransformToAncestor(scroll).Transform(default).Y;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var travel = region.ActualHeight - element.ActualHeight;

        slide.Y = top < 0 ? Math.Clamp(-top, 0, Math.Max(travel, 0)) : 0;
    }

    private static T? Ancestor<T>(DependencyObject from)
        where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(from); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }
}
