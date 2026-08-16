using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.Controls;

/// <summary>
/// C10. A 30 by 17 track with an Ellipse knob that translates 13 over 150.
/// The knob is an Ellipse because a CornerRadius cannot produce a circle.
/// </summary>
public sealed class ToggleSwitch : ButtonBase
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn),
        typeof(bool),
        typeof(ToggleSwitch),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOnChanged));

    private TranslateTransform? _knobOffset;

    static ToggleSwitch()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ToggleSwitch),
            new FrameworkPropertyMetadata(typeof(ToggleSwitch)));
    }

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Knob") is FrameworkElement knob)
        {
            // Built here rather than in the template: WPF freezes Freezables
            // declared inside a ControlTemplate, and a frozen transform cannot
            // be animated.
            _knobOffset = new TranslateTransform();
            knob.RenderTransform = _knobOffset;
            MoveKnob(animate: false);
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        IsOn = !IsOn;
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ToggleSwitch)d).MoveKnob(animate: true);

    private void MoveKnob(bool animate)
    {
        if (_knobOffset is null)
        {
            return;
        }

        var travel = IsOn ? Tokens.Number("ToggleKnobTravel") : 0d;

        if (!animate)
        {
            _knobOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _knobOffset.X = travel;
            return;
        }

        MotionService.AnimateDouble(
            _knobOffset,
            TranslateTransform.XProperty,
            travel,
            Tokens.Get<Duration>("Motion150"),
            Tokens.Get<KeySpline>("EaseStandard"));
    }
}
