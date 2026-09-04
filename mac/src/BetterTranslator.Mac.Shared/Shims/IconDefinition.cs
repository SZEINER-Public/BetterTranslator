using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Metadata;

namespace BetterTranslator.App.Controls;

public sealed class IconPart
{
    [Content]
    public Geometry? Geometry { get; set; }

    public double StrokeThickness { get; set; } = 1.2;

    public PenLineCap Cap { get; set; } = PenLineCap.Flat;

    public PenLineJoin Join { get; set; } = PenLineJoin.Miter;

    public bool Filled { get; set; }
}

public sealed class IconPartCollection : Collection<IconPart>;

public sealed class IconDefinition
{
    public double Box { get; set; } = 16;

    public double BoxHeight { get; set; } = double.NaN;

    public double EffectiveHeight => double.IsNaN(BoxHeight) ? Box : BoxHeight;

    [Content]
    public IconPartCollection Parts { get; } = [];
}
