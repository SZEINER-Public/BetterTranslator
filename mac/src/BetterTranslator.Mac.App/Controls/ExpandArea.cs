using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using BetterTranslator.App.Services;

namespace BetterTranslator.Mac.App.Controls;

public sealed class ExpandArea : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ExpandArea, bool>(nameof(IsOpen), false);

    private CancellationTokenSource? _motion;

    static ExpandArea() =>
        IsOpenProperty.Changed.AddClassHandler<ExpandArea>((area, e) => area.Apply(e.NewValue is true));

    public ExpandArea()
    {
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Top;
        Height = 0;
        IsVisible = false;
        AutomationProperties.SetIsOffscreenBehavior(this, IsOffscreenBehavior.Onscreen);
    }

    public static bool MotionEnabled { get; set; } = true;

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    private void Apply(bool open)
    {
        if (open)
        {
            IsVisible = true;
        }

        var plan = ExpandPlan.For(CurrentHeight, open ? Measured() : 0d, MotionEnabled);

        if (!plan.Animates)
        {
            Settle(open);
            return;
        }

        Run(plan, open);
    }

    private async void Run(ExpandPlan plan, bool open)
    {
        _motion?.Cancel();
        _motion?.Dispose();

        var motion = new CancellationTokenSource();
        _motion = motion;

        var animation = new Animation
        {
            Duration = Tokens.Get<TimeSpan>("Motion180"),
            Easing = Tokens.Get<Easing>("EaseStandard"),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(HeightProperty, plan.From) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(HeightProperty, plan.To) } },
            },
        };

        try
        {
            await animation.RunAsync(this, motion.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (motion.IsCancellationRequested)
        {
            return;
        }

        Settle(open);
    }

    private void Settle(bool open)
    {
        if (IsOpen != open)
        {
            return;
        }

        _motion?.Cancel();
        _motion?.Dispose();
        _motion = null;

        Height = open ? double.NaN : 0d;
        IsVisible = open;
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

            return Bounds.Height;
        }
    }

    private double Measured()
    {
        if (Content is not Control child)
        {
            return 0d;
        }

        child.Measure(new Size(Bounds.Width > 0 ? Bounds.Width : double.PositiveInfinity, double.PositiveInfinity));

        return child.DesiredSize.Height;
    }
}
