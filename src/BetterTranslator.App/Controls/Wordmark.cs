using System.Windows;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

/// <summary>
/// The product mark: six bars in two columns and three rows, row two in the
/// accent, the bottom-right bar deliberately short. It is not a currentColor
/// icon, so it does not go through <see cref="IconView"/>.
///
/// Set <see cref="AccentBrush"/> to null for the muted variant used in empty
/// states, where every bar takes <see cref="BarBrush"/>.
/// </summary>
public sealed class Wordmark : FrameworkElement
{
    internal const double DesignWidth = 822;
    internal const double DesignHeight = 385;

    /// <summary>x, y, width, height on the 822 by 385 design grid.</summary>
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

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush),
        typeof(Brush),
        typeof(Wordmark),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(Wordmark),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Mark height in device-independent pixels; width follows the ratio.</summary>
    public static readonly DependencyProperty MarkHeightProperty = DependencyProperty.Register(
        nameof(MarkHeight),
        typeof(double),
        typeof(Wordmark),
        new FrameworkPropertyMetadata(
            12d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public double MarkHeight
    {
        get => (double)GetValue(MarkHeightProperty);
        set => SetValue(MarkHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(MarkHeight * (DesignWidth / DesignHeight), MarkHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var scale = MarkHeight / DesignHeight;

        foreach (var bar in DarkBars)
        {
            Draw(drawingContext, BarBrush, bar, scale);
        }

        // No accent means the muted variant: one colour across all six bars.
        var rowTwo = AccentBrush ?? BarBrush;
        foreach (var bar in AccentBars)
        {
            Draw(drawingContext, rowTwo, bar, scale);
        }
    }

    private static void Draw(
        DrawingContext drawingContext,
        Brush brush,
        (double X, double Y, double W, double H) bar,
        double scale) =>
        drawingContext.DrawRectangle(
            brush,
            null,
            new Rect(bar.X * scale, bar.Y * scale, bar.W * scale, bar.H * scale));
}
