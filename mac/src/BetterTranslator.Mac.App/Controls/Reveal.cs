using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using BetterTranslator.App.Services;

namespace BetterTranslator.Mac.App.Controls;

public static class Reveal
{
    public static readonly AttachedProperty<bool> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Target", typeof(Reveal));

    static Reveal()
    {
        TargetProperty.Changed.AddClassHandler<Control, bool>(OnTargetChanged);
    }

    public static void SetTarget(Control element, bool value) => element.SetValue(TargetProperty, value);

    public static bool GetTarget(Control element) => element.GetValue(TargetProperty);

    private static void OnTargetChanged(Control element, AvaloniaPropertyChangedEventArgs<bool> e)
    {
        element.PropertyChanged -= OnElementPropertyChanged;

        if (e.GetNewValue<bool>())
        {
            element.PropertyChanged += OnElementPropertyChanged;
        }
    }

    private static void OnElementPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not Control element ||
            e.Property != Visual.IsVisibleProperty ||
            !e.GetNewValue<bool>())
        {
            return;
        }

        element.Opacity = 1;

        var fade = new Animation
        {
            Duration = Tokens.Get<TimeSpan>("Motion200"),
            Easing = Tokens.Get<Easing>("EaseStandard"),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(Visual.OpacityProperty, Tokens.Number("RevealFromOpacity")) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(Visual.OpacityProperty, 1d) },
                },
            },
        };

        _ = fade.RunAsync(element);
    }
}
