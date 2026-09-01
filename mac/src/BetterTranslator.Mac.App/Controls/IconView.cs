using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BetterTranslator.App.Controls;

namespace BetterTranslator.Mac.App.Controls;

public sealed class IconView : Control
{
    public static readonly StyledProperty<IconDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<IconView, IconDefinition?>(nameof(Definition));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<IconView>();

    static IconView()
    {
        AffectsMeasure<IconView>(DefinitionProperty);
        AffectsRender<IconView>(DefinitionProperty, ForegroundProperty);
    }

    public IconDefinition? Definition
    {
        get => GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        Definition is null ? default : new Size(Definition.Box, Definition.EffectiveHeight);

    public override void Render(DrawingContext context)
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
                context.DrawGeometry(Foreground, null, part.Geometry);
                continue;
            }

            var pen = new ImmutablePen(
                Foreground.ToImmutable(),
                part.StrokeThickness,
                null,
                part.Cap,
                part.Join);

            context.DrawGeometry(null, pen, part.Geometry);
        }
    }
}
