using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using BetterTranslator.App.Services;

namespace BetterTranslator.Mac.App.Controls;

public static class Disclosure
{
    public static readonly AttachedProperty<bool> TurnsWhenOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("TurnsWhenOpen", typeof(Disclosure));

    static Disclosure()
    {
        TurnsWhenOpenProperty.Changed.AddClassHandler<Control, bool>(OnTurnsWhenOpenChanged);
    }

    public static void SetTurnsWhenOpen(Control element, bool value) =>
        element.SetValue(TurnsWhenOpenProperty, value);

    public static bool GetTurnsWhenOpen(Control element) =>
        element.GetValue(TurnsWhenOpenProperty);

    private static void OnTurnsWhenOpenChanged(Control element, AvaloniaPropertyChangedEventArgs<bool> e)
    {
        if (element.RenderTransform is not RotateTransform turn)
        {
            turn = new RotateTransform();
            element.RenderTransform = turn;
        }

        turn.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = RotateTransform.AngleProperty,
                Duration = Tokens.Get<TimeSpan>("Motion120"),
                Easing = Tokens.Get<Easing>("EaseStandard"),
            },
        };

        turn.Angle = e.GetNewValue<bool>() ? 180d : 0d;
    }
}
