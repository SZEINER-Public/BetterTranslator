using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BetterTranslator.App.Services;

namespace BetterTranslator.App.Controls;

/// <summary>
/// What stands in the result region while a translation is in flight: bars for
/// the lines the answer will occupy, swept by one travelling mask.
///
/// It carries no text and no binding that could reach any. That is the point.
/// The region reserved for the translation used to be filled with the source
/// for the whole of the wait, which reads as an answer and is not one, and a
/// placeholder that cannot hold a string cannot regress to that.
/// </summary>
public sealed class SkeletonLines : Control
{
    /// <summary>
    /// Where the mask's thin band sits within the gradient, and how wide it is.
    /// Mask geometry rather than a design value: the depth of the thinning is
    /// the token, the shape of the ramp is what makes it a sweep at all.
    /// </summary>
    private const double BandStart = 0.35;
    private const double BandCentre = 0.5;
    private const double BandEnd = 0.65;

    /// <summary>
    /// One full travel of the mask, in the brush's own relative space. From one
    /// width off the left to one width off the right, so both ends of the loop
    /// are the untouched bars and the wrap cannot be seen.
    /// </summary>
    private const double SweepFrom = -1;
    private const double SweepTo = 1;

    public static readonly DependencyProperty SourceLengthProperty = DependencyProperty.Register(
        nameof(SourceLength),
        typeof(int),
        typeof(SkeletonLines),
        new FrameworkPropertyMetadata(0, OnSourceLengthChanged));

    public static readonly DependencyProperty LineLengthsProperty = DependencyProperty.Register(
        nameof(LineLengths),
        typeof(IReadOnlyList<int>),
        typeof(SkeletonLines),
        new FrameworkPropertyMetadata(null, OnSourceLengthChanged));

    private Panel? _lines;
    private LinearGradientBrush? _mask;
    private TranslateTransform? _sweep;
    private Window? _window;

    static SkeletonLines()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SkeletonLines),
            new FrameworkPropertyMetadata(typeof(SkeletonLines)));
    }

    public SkeletonLines()
    {
        // Collapsed is the state this spends nearly all its life in, and a loop
        // running behind a hidden region is frames spent on nothing.
        IsVisibleChanged += (_, _) => Sync();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// How many characters were sent. The bars are laid out from it, so the
    /// placeholder approximates the block that is coming rather than being a
    /// fixed shape every answer has to grow or shrink away from.
    /// </summary>
    public int SourceLength
    {
        get => (int)GetValue(SourceLengthProperty);
        set => SetValue(SourceLengthProperty, value);
    }

    /// <summary>
    /// The length of every line the answer will occupy, when the caller knows
    /// them. One bar per line, each as wide as its own line, so the placeholder
    /// is the shape of the message being translated rather than a guess at it --
    /// and a reader watching a long message can see which line is which.
    ///
    /// Null or empty falls back to <see cref="SourceLength"/>, which is all a
    /// single-line send can say about itself.
    /// </summary>
    public IReadOnlyList<int>? LineLengths
    {
        get => (IReadOnlyList<int>?)GetValue(LineLengthsProperty);
        set => SetValue(LineLengthsProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _lines = GetTemplateChild("PART_Lines") as Panel;
        Build();
    }

    private static void OnSourceLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SkeletonLines)d).Build();

    /// <summary>
    /// Lays the bars out once per source length, not per frame. The bars are
    /// the visuals the loop runs over; rebuilding them while it runs would be a
    /// new visual per tick, which is what makes placeholder animations expensive
    /// everywhere they are expensive.
    /// </summary>
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
        var fill = Tokens.Get<Brush>("BrushSkeletonBar");
        var radius = Tokens.Get<CornerRadius>("RadiusXs");

        // Half the difference above and half below, so a bar sits on the same
        // line box the translation will and the swap moves nothing.
        var gap = Math.Max(pitch - height, 0) / 2;

        var min = Tokens.Number("SkeletonLastBarMinFraction");
        var max = Tokens.Number("SkeletonLastBarMaxFraction");

        if (LineLengths is { Count: > 0 } lengths)
        {
            // No cap: the placeholder occupies the room the answer will, so a
            // long message scrolls the same either way and nothing jumps when
            // the translation lands.
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

        // A rebuild replaces the transform the loop was running on, so the loop
        // has to be put back onto the new one or the mask parks mid-sweep.
        ApplyMask();
        Sync();
    }

    /// <summary>
    /// How many bars, and how long the closing one is as a fraction of the
    /// width. Separated from the visuals because it is the part that can be
    /// wrong without looking wrong: a placeholder of the wrong height is a
    /// layout shift at the moment the answer lands, which is exactly when
    /// nobody is looking at the placeholder.
    /// </summary>
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

        // What is left over after the full lines. The one thing a character
        // count can honestly say about the shape of the block coming back.
        var remainder = sourceLength - ((lines - 1) * charactersPerLine);

        return (lines, Math.Clamp(remainder / charactersPerLine, minFraction, maxFraction));
    }

    /// <summary>
    /// A bar taking a fraction of the width. Star weights rather than a width:
    /// the region is fluid, so a bar has to stay a fraction of it at every window
    /// size and every DPI.
    /// </summary>
    private static Grid Proportional(
        Brush fill,
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

    private static Border Bar(Brush fill, CornerRadius radius, double height, double gap) => new()
    {
        Height = height,
        Background = fill,
        CornerRadius = radius,
        Margin = new Thickness(0, gap, 0, gap),
        SnapsToDevicePixels = true,
    };

    /// <summary>
    /// The shimmer, as a mask. One gradient of black at two alphas travels
    /// across the bars and thins them as it passes; nothing is painted over
    /// them, nothing glows, and the bars keep their single flat token fill. An
    /// effect layer here would be a drop shadow with the colour turned up.
    /// </summary>
    private void ApplyMask()
    {
        if (_lines is null)
        {
            return;
        }

        // Black is the only colour a mask has: the alpha is the whole of it, so
        // this is not a palette value and does not belong in the colour tokens.
        var solid = Color.FromArgb(byte.MaxValue, 0, 0, 0);
        var thin = Color.FromArgb(
            (byte)Math.Round(byte.MaxValue * (1 - Tokens.Number("SkeletonShimmerDepth"))),
            0,
            0,
            0);

        // Built here rather than in the template: WPF freezes Freezables
        // declared inside a ControlTemplate, and a frozen transform cannot be
        // animated.
        _sweep = new TranslateTransform();

        _mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = _sweep,
        };

        _mask.GradientStops.Add(new GradientStop(solid, 0));
        _mask.GradientStops.Add(new GradientStop(solid, BandStart));
        _mask.GradientStops.Add(new GradientStop(thin, BandCentre));
        _mask.GradientStops.Add(new GradientStop(solid, BandEnd));
        _mask.GradientStops.Add(new GradientStop(solid, 1));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A loop behind a window that is not in front is the same waste as one
        // behind a hidden region, and the window is the only thing that knows.
        _window = Window.GetWindow(this);

        if (_window is not null)
        {
            _window.Activated += OnWindowActivationChanged;
            _window.Deactivated += OnWindowActivationChanged;
        }

        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivationChanged;
            _window.Deactivated -= OnWindowActivationChanged;
            _window = null;
        }

        Stop();
    }

    private void OnWindowActivationChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        if (IsVisible && (_window?.IsActive ?? true))
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        if (_sweep is null || _lines is null)
        {
            return;
        }

        var running = MotionService.BeginLoop(
            _sweep,
            TranslateTransform.XProperty,
            SweepFrom,
            SweepTo,
            Tokens.Get<Duration>("Motion1200"));

        // With motion off the bars stay and the mask never goes on, so what is
        // left is the static tinted placeholder rather than a frozen band
        // sitting across the middle of it.
        _lines.OpacityMask = running ? _mask : null;
    }

    private void Stop()
    {
        if (_sweep is null)
        {
            return;
        }

        // Parked one width off, where the mask is wholly opaque, so a stopped
        // placeholder is the same untouched bars a reduced-motion one is.
        MotionService.EndLoop(_sweep, TranslateTransform.XProperty, SweepFrom);
    }
}
