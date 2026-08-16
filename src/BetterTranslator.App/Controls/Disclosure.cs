using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.Controls;

public static class Disclosure
{
    public static readonly DependencyProperty TurnsWhenOpenProperty = DependencyProperty.RegisterAttached(
        "TurnsWhenOpen",
        typeof(bool),
        typeof(Disclosure),
        new PropertyMetadata(false, OnTurnsWhenOpenChanged));

    public static void SetTurnsWhenOpen(DependencyObject element, bool value) =>
        element.SetValue(TurnsWhenOpenProperty, value);

    public static bool GetTurnsWhenOpen(DependencyObject element) =>
        (bool)element.GetValue(TurnsWhenOpenProperty);

    private static void OnTurnsWhenOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if (element.RenderTransform is not RotateTransform turn)
        {
            turn = new RotateTransform();
            element.RenderTransform = turn;
        }

        MotionService.AnimateDouble(
            turn,
            RotateTransform.AngleProperty,
            e.NewValue is true ? 180d : 0d,
            Tokens.Get<Duration>("Motion120"),
            Tokens.Get<KeySpline>("EaseStandard"));
    }
}
