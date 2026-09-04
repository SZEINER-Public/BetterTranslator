using Avalonia.Media;

namespace System.Windows.Media;

public class Brush(IBrush value, Color color) : IEquatable<Brush>
{
    public IBrush Value { get; } = value;

    public Color Color { get; } = color;

    public static implicit operator Avalonia.Media.Brush?(Brush? brush) => brush?.Value as Avalonia.Media.Brush;

    public bool Equals(Brush? other) => other is not null && Color.Equals(other.Color);

    public override bool Equals(object? obj) => Equals(obj as Brush);

    public override int GetHashCode() => Color.GetHashCode();

    public override string ToString() => Color.ToString();
}

public sealed class SolidColorBrush : Brush
{
    public SolidColorBrush(Color color)
        : base(new Avalonia.Media.SolidColorBrush(color), color)
    {
    }

    public SolidColorBrush(ISolidColorBrush source)
        : base(source, source.Color)
    {
    }
}
