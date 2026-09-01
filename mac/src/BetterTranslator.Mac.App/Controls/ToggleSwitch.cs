using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using BetterTranslator.App.Services;

namespace BetterTranslator.Mac.App.Controls;

public sealed class ToggleSwitch : Button
{
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<ToggleSwitch, bool>(
            nameof(IsOn),
            false,
            defaultBindingMode: BindingMode.TwoWay);

    private TranslateTransform? _knobOffset;

    public bool IsOn
    {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ToggleSwitch);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (e.NameScope.Find<Control>("PART_Knob") is { } knob)
        {
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsOnProperty)
        {
            return;
        }

        PseudoClasses.Set(":on", change.GetNewValue<bool>());
        MoveKnob(animate: true);
    }

    private void MoveKnob(bool animate)
    {
        if (_knobOffset is null)
        {
            return;
        }

        var travel = IsOn ? Tokens.Number("ToggleKnobTravel") : 0d;

        if (!animate)
        {
            _knobOffset.Transitions = null;
            _knobOffset.X = travel;
            return;
        }

        _knobOffset.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = Tokens.Get<TimeSpan>("Motion150"),
                Easing = Tokens.Get<Easing>("EaseStandard"),
            },
        };

        _knobOffset.X = travel;
    }
}
