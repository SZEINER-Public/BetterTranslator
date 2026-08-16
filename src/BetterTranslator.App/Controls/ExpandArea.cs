using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.Controls;

public sealed class ExpandArea : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen),
        typeof(bool),
        typeof(ExpandArea),
        new PropertyMetadata(false, OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public ExpandArea()
    {
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Top;
        Height = 0;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetIsOffscreenBehavior(this, IsOffscreenBehavior.Onscreen);
    }

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ExpandArea area)
        {
            area.Apply(e.NewValue is true);
        }
    }

    private void Apply(bool open)
    {
        if (open)
        {
            Visibility = Visibility.Visible;
        }

        var plan = ExpandPlan.For(CurrentHeight, open ? Measured() : 0d, MotionService.Enabled);

        if (!plan.Animates)
        {
            Settle(open);
            return;
        }

        MotionService.AnimateDouble(
            this,
            HeightProperty,
            plan.To,
            Tokens.Get<Duration>("Motion180"),
            Tokens.Get<System.Windows.Media.Animation.KeySpline>("EaseStandard"),
            () => Settle(open));
    }

    private void Settle(bool open)
    {
        if (IsOpen != open)
        {
            return;
        }

        BeginAnimation(HeightProperty, null);
        Height = open ? double.NaN : 0d;
        Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private double CurrentHeight
    {
        get
        {
            var height = Height;

            if (!double.IsNaN(height))
            {
                return height;
            }

            return ActualHeight;
        }
    }

    private double Measured()
    {
        if (Content is not UIElement child)
        {
            return 0d;
        }

        child.Measure(new Size(ActualWidth > 0 ? ActualWidth : double.PositiveInfinity, double.PositiveInfinity));

        return child.DesiredSize.Height;
    }
}
