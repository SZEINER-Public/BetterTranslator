using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

/// <summary>
/// Draws an <see cref="IconDefinition"/> in the inherited foreground, so hover,
/// pressed and disabled states recolour a glyph without the caller wiring
/// anything up. It renders directly rather than through a template: an icon is
/// a handful of geometries, and a Path per part in an ItemsControl would cost
/// far more than it buys.
/// </summary>
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty DefinitionProperty = DependencyProperty.Register(
        nameof(Definition),
        typeof(IconDefinition),
        typeof(IconView),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Inherited from the surrounding text foreground, the same way a
    /// currentColor SVG behaves in the browser.
    /// </summary>
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(
            typeof(IconView),
            new FrameworkPropertyMetadata(
                SystemColors.ControlTextBrush,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public IconDefinition? Definition
    {
        get => (IconDefinition?)GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Definition is null ? default : new Size(Definition.Box, Definition.EffectiveHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Definition is null || Foreground is null)
        {
            return;
        }

        foreach (var part in Definition.Parts)
        {
            if (part.Geometry is null)
            {
                continue;
            }

            if (part.Filled)
            {
                drawingContext.DrawGeometry(Foreground, null, part.Geometry);
                continue;
            }

            var pen = new Pen(Foreground, part.StrokeThickness)
            {
                StartLineCap = part.Cap,
                EndLineCap = part.Cap,
                LineJoin = part.Join,
            };

            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, part.Geometry);
        }
    }
}
