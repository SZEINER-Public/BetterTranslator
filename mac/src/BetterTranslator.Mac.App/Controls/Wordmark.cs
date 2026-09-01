using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BetterTranslator.Mac.App.Controls;

public sealed class Wordmark : Control
{
    internal const double DesignWidth = 822;
    internal const double DesignHeight = 385;

    internal static readonly (double X, double Y, double W, double H)[] DarkBars =
    [
        (0, 0, 378, 77),
        (444, 0, 378, 77),
        (0, 308, 378, 77),
        (444, 308, 240, 77),
    ];

    internal static readonly (double X, double Y, double W, double H)[] AccentBars =
    [
        (0, 154, 378, 77),
        (444, 154, 378, 77),
    ];

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<Wordmark, IBrush?>(nameof(BarBrush), Brushes.Black);

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<Wordmark, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<double> MarkHeightProperty =
        AvaloniaProperty.Register<Wordmark, double>(nameof(MarkHeight), 12d);

    static Wordmark()
    {
        AffectsRender<Wordmark>(BarBrushProperty, AccentBrushProperty, MarkHeightProperty);
        AffectsMeasure<Wordmark>(MarkHeightProperty);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public double MarkHeight
    {
        get => GetValue(MarkHeightProperty);
        set => SetValue(MarkHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(MarkHeight * (DesignWidth / DesignHeight), MarkHeight);

    public override void Render(DrawingContext context)
    {
        var scale = MarkHeight / DesignHeight;

        foreach (var bar in DarkBars)
        {
            Draw(context, BarBrush, bar, scale);
        }

        var rowTwo = AccentBrush ?? BarBrush;

        foreach (var bar in AccentBars)
        {
            Draw(context, rowTwo, bar, scale);
        }
    }

    private static void Draw(
        DrawingContext context,
        IBrush? brush,
        (double X, double Y, double W, double H) bar,
        double scale) =>
        context.DrawRectangle(
            brush,
            null,
            new Rect(bar.X * scale, bar.Y * scale, bar.W * scale, bar.H * scale));
}
