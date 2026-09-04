using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using BetterTranslator.App.Services;

namespace BetterTranslator.Mac.App.Controls;

public sealed class SkeletonLines : TemplatedControl
{
    private const double BandStart = 0.35;
    private const double BandCentre = 0.5;
    private const double BandEnd = 0.65;

    private const double SweepFrom = -1;
    private const double SweepTo = 1;

    public static readonly StyledProperty<int> SourceLengthProperty =
        AvaloniaProperty.Register<SkeletonLines, int>(nameof(SourceLength), 0);

    public static readonly StyledProperty<IReadOnlyList<int>?> LineLengthsProperty =
        AvaloniaProperty.Register<SkeletonLines, IReadOnlyList<int>?>(nameof(LineLengths));

    private static readonly StyledProperty<double> SweepProperty =
        AvaloniaProperty.Register<SkeletonLines, double>(nameof(Sweep), SweepFrom);

    private Panel? _lines;
    private LinearGradientBrush? _mask;
    private TranslateTransform? _sweep;
    private WindowBase? _window;
    private CancellationTokenSource? _loop;

    static SkeletonLines()
    {
        SourceLengthProperty.Changed.AddClassHandler<SkeletonLines>((lines, _) => lines.Build());
        LineLengthsProperty.Changed.AddClassHandler<SkeletonLines>((lines, _) => lines.Build());
    }

    public int SourceLength
    {
        get => GetValue(SourceLengthProperty);
        set => SetValue(SourceLengthProperty, value);
    }

    public IReadOnlyList<int>? LineLengths
    {
        get => GetValue(LineLengthsProperty);
        set => SetValue(LineLengthsProperty, value);
    }

    private double Sweep
    {
        get => GetValue(SweepProperty);
        set => SetValue(SweepProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SkeletonLines);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _lines = e.NameScope.Find<Panel>("PART_Lines");
        Build();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SweepProperty || change.Property == BoundsProperty)
        {
            ApplySweep();
            return;
        }

        if (change.Property == IsVisibleProperty)
        {
            Sync();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _window = e.Root as WindowBase;

        if (_window is not null)
        {
            _window.Activated += OnWindowActivationChanged;
            _window.Deactivated += OnWindowActivationChanged;
        }

        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_window is not null)
        {
            _window.Activated -= OnWindowActivationChanged;
            _window.Deactivated -= OnWindowActivationChanged;
            _window = null;
        }

        Stop();
    }

    private void Build()
    {
        if (_lines is null)
        {
            return;
        }

        _lines.Children.Clear();

        var pitch = Tokens.Number("ProseLineHeight");
        var height = Tokens.Number("SkeletonBarHeight");
        var perLine = Tokens.Number("SkeletonCharsPerLine");
        var fill = Fill();
        var radius = Tokens.Get<CornerRadius>("RadiusXs");

        var gap = Math.Max(pitch - height, 0) / 2;

        var min = Tokens.Number("SkeletonLastBarMinFraction");
        var max = Tokens.Number("SkeletonLastBarMaxFraction");

        if (LineLengths is { Count: > 0 } lengths)
        {
            foreach (var length in lengths)
            {
                _lines.Children.Add(Proportional(
                    fill,
                    radius,
                    height,
                    gap,
                    Math.Clamp(length / perLine, min, max)));
            }

            ApplyMask();
            Sync();
            return;
        }

        var (count, fraction) = Shape(
            SourceLength,
            perLine,
            (int)Tokens.Number("SkeletonMaxLines"),
            min,
            max);

        for (var line = 0; line < count - 1; line++)
        {
            _lines.Children.Add(Bar(fill, radius, height, gap));
        }

        _lines.Children.Add(Proportional(fill, radius, height, gap, fraction));

        ApplyMask();
        Sync();
    }

    internal static (int Lines, double LastFraction) Shape(
        int sourceLength,
        double charactersPerLine,
        int maxLines,
        double minFraction,
        double maxFraction)
    {
        var lines = Math.Clamp(
            (int)Math.Ceiling(Math.Max(sourceLength, 1) / charactersPerLine),
            1,
            maxLines);

        var remainder = sourceLength - ((lines - 1) * charactersPerLine);

        return (lines, Math.Clamp(remainder / charactersPerLine, minFraction, maxFraction));
    }

    private static Grid Proportional(
        IBrush? fill,
        CornerRadius radius,
        double height,
        double gap,
        double fraction)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        row.Children.Add(Bar(fill, radius, height, gap));

        return row;
    }

    private static Border Bar(IBrush? fill, CornerRadius radius, double height, double gap) => new()
    {
        Height = height,
        Background = fill,
        CornerRadius = radius,
        Margin = new Thickness(0, gap, 0, gap),
        UseLayoutRounding = true,
    };

    private IBrush? Fill() =>
        this.TryFindResource("BrushSkeletonBar", out var value) && value is IBrush brush ? brush : null;

    private void ApplyMask()
    {
        if (_lines is null)
        {
            return;
        }

        StopLoop();

        var solid = Color.FromArgb(byte.MaxValue, 0, 0, 0);
        var thin = Color.FromArgb(
            (byte)Math.Round(byte.MaxValue * (1 - Tokens.Number("SkeletonShimmerDepth"))),
            0,
            0,
            0);

        _sweep = new TranslateTransform();

        _mask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            Transform = _sweep,
        };

        _mask.GradientStops.Add(new GradientStop(solid, 0));
        _mask.GradientStops.Add(new GradientStop(solid, BandStart));
        _mask.GradientStops.Add(new GradientStop(thin, BandCentre));
        _mask.GradientStops.Add(new GradientStop(solid, BandEnd));
        _mask.GradientStops.Add(new GradientStop(solid, 1));

        ApplySweep();
    }

    private void OnWindowActivationChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        if (IsEffectivelyVisible && (_window?.IsActive ?? true))
        {
            Start();
            return;
        }

        Stop();
    }

    private void Start()
    {
        if (_sweep is null || _lines is null || _loop is not null)
        {
            return;
        }

        var animation = new Animation
        {
            Duration = Tokens.Get<TimeSpan>("Motion1200"),
            IterationCount = IterationCount.Infinite,
            Easing = new LinearEasing(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(SweepProperty, SweepFrom) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(SweepProperty, SweepTo) } },
            },
        };

        _loop = new CancellationTokenSource();
        _ = animation.RunAsync(this, _loop.Token);

        _lines.OpacityMask = _mask;
    }

    private void Stop()
    {
        StopLoop();

        if (_sweep is null)
        {
            return;
        }

        Sweep = SweepFrom;
        ApplySweep();
    }

    private void StopLoop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    private void ApplySweep()
    {
        if (_sweep is null || _lines is null)
        {
            return;
        }

        _sweep.X = Sweep * _lines.Bounds.Width;
        _lines.InvalidateVisual();
    }
}
