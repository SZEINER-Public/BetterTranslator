using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterTranslator.Tools.AppIcon;

internal readonly record struct Bar(double X, double Y, double W, double H, uint Colour);

internal sealed class MarkSource
{
    private const int GroundTolerance = 60;

    public required double MasterSize { get; init; }

    public required uint AccentColour { get; init; }

    public required uint GroundColour { get; init; }

    public required double GroundRadius { get; init; }

    public required IReadOnlyList<Bar> Bars { get; init; }

    public bool HasGround => (GroundColour >> 24) >= 128;

    public double MarkX => Bars.Min(bar => bar.X);

    public double MarkY => Bars.Min(bar => bar.Y);

    public double MarkWidth => Bars.Max(bar => bar.X + bar.W) - MarkX;

    public double MarkHeight => Bars.Max(bar => bar.Y + bar.H) - MarkY;

    public static MarkSource Load(string path)
    {
        var frame = BitmapFrame.Create(
            new Uri(System.IO.Path.GetFullPath(path)),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var width = frame.PixelWidth;
        var height = frame.PixelHeight;

        if (width != height)
        {
            throw new InvalidDataException($"{path} is {width} by {height}; the master mark must be square.");
        }

        if (width < 256)
        {
            throw new InvalidDataException($"{path} is {width} pixels across; the master mark must be at least 256.");
        }

        var pixels = new byte[width * height * 4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, width * 4, 0);

        uint At(int x, int y)
        {
            var offset = ((y * width) + x) * 4;
            return ((uint)pixels[offset + 3] << 24)
                | ((uint)pixels[offset + 2] << 16)
                | ((uint)pixels[offset + 1] << 8)
                | pixels[offset];
        }

        var edge = At(width / 2, 0);
        var ground = (edge >> 24) >= 128 ? edge : At(0, 0);

        bool IsInk(uint colour)
        {
            if ((colour >> 24) < 128)
            {
                return false;
            }

            if ((ground >> 24) < 128)
            {
                return true;
            }

            var distance =
                Math.Abs((int)((colour >> 16) & 0xFF) - (int)((ground >> 16) & 0xFF)) +
                Math.Abs((int)((colour >> 8) & 0xFF) - (int)((ground >> 8) & 0xFF)) +
                Math.Abs((int)(colour & 0xFF) - (int)(ground & 0xFF));

            return distance > GroundTolerance;
        }

        var bars = new List<Bar>();
        foreach (var (top, bottom) in Bands(y => Enumerable.Range(0, width).Any(x => IsInk(At(x, y))), height))
        {
            var scan = (top + bottom) / 2;
            foreach (var (left, right) in Bands(x => IsInk(At(x, scan)), width))
            {
                bars.Add(new Bar(left, top, right - left + 1, bottom - top + 1, Modal(At, (left + right) / 2, top, bottom)));
            }
        }

        if (bars.Count == 0)
        {
            throw new InvalidDataException($"{path} carries no mark bars.");
        }

        var colours = bars
            .GroupBy(bar => bar.Colour)
            .OrderBy(group => group.Count())
            .ToList();

        if (colours.Count != 2)
        {
            throw new InvalidDataException($"{path} carries {colours.Count} bar colours; the mark has two.");
        }

        return new MarkSource
        {
            MasterSize = width,
            AccentColour = colours[0].Key,
            GroundColour = ground,
            GroundRadius = (ground >> 24) >= 128 ? CornerRadius(At, width) : 0,
            Bars = bars,
        };
    }

    private static double CornerRadius(Func<int, int, uint> at, int size)
    {
        var uncovered = 0d;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                uncovered += 1 - (((at(x, y) >> 24) & 0xFF) / 255d);
            }
        }

        return Math.Round(Math.Sqrt(uncovered / (4 - Math.PI)), MidpointRounding.AwayFromZero);
    }

    private static IEnumerable<(int Start, int End)> Bands(Func<int, bool> occupied, int length)
    {
        var open = false;
        var start = 0;

        for (var index = 0; index <= length; index++)
        {
            var here = index < length && occupied(index);

            if (here && !open)
            {
                open = true;
                start = index;
            }
            else if (!here && open)
            {
                open = false;
                yield return (start, index - 1);
            }
        }
    }

    private static uint Modal(Func<int, int, uint> at, int x, int top, int bottom) =>
        Enumerable
            .Range(top, bottom - top + 1)
            .Select(y => at(x, y))
            .GroupBy(colour => colour)
            .OrderByDescending(group => group.Count())
            .First()
            .Key;
}
