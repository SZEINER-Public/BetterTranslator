using System.Collections.ObjectModel;
using System.Windows.Markup;
using System.Windows.Media;

namespace BetterTranslator.App.Controls;

/// <summary>
/// One stroked or filled piece of an icon. Icons in this set are tuned per box
/// size, and several mix stroke weights inside a single icon, so a part -- not
/// the icon -- owns the stroke thickness and the caps.
/// </summary>
[ContentProperty(nameof(Geometry))]
public sealed class IconPart
{
    public Geometry? Geometry { get; set; }

    public double StrokeThickness { get; set; } = 1.2;

    public PenLineCap Cap { get; set; } = PenLineCap.Flat;

    public PenLineJoin Join { get; set; } = PenLineJoin.Miter;

    /// <summary>True for a solid part, such as the dots in more-horizontal.</summary>
    public bool Filled { get; set; }
}

public sealed class IconPartCollection : Collection<IconPart>;

/// <summary>
/// An icon as authored: a square box and the parts drawn inside it. The box is
/// the icon's own design size, which varies from 9 to 24 across the set. The
/// weights were tuned per size so they read evenly at 100 percent; do not
/// normalise them.
/// </summary>
[ContentProperty(nameof(Parts))]
public sealed class IconDefinition
{
    /// <summary>Design width of the icon box.</summary>
    public double Box { get; set; } = 16;

    /// <summary>Design height. Equal to <see cref="Box"/> unless set.</summary>
    public double BoxHeight { get; set; } = double.NaN;

    public double EffectiveHeight => double.IsNaN(BoxHeight) ? Box : BoxHeight;

    public IconPartCollection Parts { get; } = [];
}
