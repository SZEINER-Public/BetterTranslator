namespace BetterTranslator.Tools.AppIcon;

internal readonly record struct PlacedBar(int X, int Y, int W, int H, uint Colour);

internal sealed class FrameLayout
{
    public required int Size { get; init; }

    public required bool AccentRowOnly { get; init; }

    public required uint GroundColour { get; init; }

    public required double GroundRadius { get; init; }

    public required IReadOnlyList<PlacedBar> Bars { get; init; }

    public bool HasGround => (GroundColour >> 24) >= 128;

    public static FrameLayout For(MarkSource mark, int size, bool accentRowOnly)
    {
        var scale = size / mark.MasterSize;

        var columns = mark.Bars.Select(bar => bar.X).Distinct().Order().ToList();
        var rows = mark.Bars.Select(bar => bar.Y).Distinct().Order().ToList();

        var columnPitch = columns.Count > 1 ? columns[1] - columns[0] : mark.MarkWidth;
        var rowPitch = rows.Count > 1 ? (rows[^1] - rows[0]) / (rows.Count - 1) : mark.MarkHeight;
        var barHeight = mark.Bars.Average(bar => bar.H);

        var drawn = accentRowOnly
            ? mark.Bars.Where(bar => bar.Colour == mark.AccentColour).ToList()
            : mark.Bars.ToList();

        var barHeightPx = accentRowOnly
            ? Snap(barHeight * rows.Count * scale)
            : Snap(barHeight * scale);

        var widthsPx = drawn.ToDictionary(bar => bar, bar => Snap(bar.W * scale));

        var columnPitchPx = Math.Max(widthsPx.Values.Max() + 1, Snap(columnPitch * scale));
        var rowPitchPx = Math.Max(barHeightPx + 1, Snap(rowPitch * scale));

        var drawnRows = drawn.Select(bar => bar.Y).Distinct().Order().ToList();

        var placed = drawn
            .Select(bar => new PlacedBar(
                columns.IndexOf(bar.X) * columnPitchPx,
                drawnRows.IndexOf(bar.Y) * rowPitchPx,
                widthsPx[bar],
                barHeightPx,
                bar.Colour))
            .ToList();

        var originX = (size - placed.Max(bar => bar.X + bar.W)) / 2;
        var originY = (size - placed.Max(bar => bar.Y + bar.H)) / 2;

        return new FrameLayout
        {
            Size = size,
            AccentRowOnly = accentRowOnly,
            GroundColour = mark.GroundColour,
            GroundRadius = mark.GroundRadius / mark.MasterSize * size,
            Bars = placed
                .Select(bar => bar with { X = bar.X + originX, Y = bar.Y + originY })
                .ToList(),
        };
    }

    private static int Snap(double value) =>
        Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
}
