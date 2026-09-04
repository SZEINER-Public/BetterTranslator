using SkiaSharp;

namespace BetterTranslator.Mac.Parity;

public sealed record DiffOutcome(bool Produced, double Ratio, string Detail);

public static class Diff
{
    private const byte ChannelTolerance = 8;

    public static DiffOutcome Write(string leftPath, string rightPath, string diffPath)
    {
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            return new DiffOutcome(false, 0, "one side is missing on this host");
        }

        using var left = SKBitmap.Decode(leftPath);
        using var right = SKBitmap.Decode(rightPath);

        if (left is null || right is null)
        {
            return new DiffOutcome(false, 0, "a frame could not be decoded");
        }

        var width = Math.Min(left.Width, right.Width);
        var height = Math.Min(left.Height, right.Height);

        using var diff = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var differing = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = left.GetPixel(x, y);
                var b = right.GetPixel(x, y);

                if (Within(a, b))
                {
                    var grey = (byte)(255 - ((255 - a.Red) / 6));
                    diff.SetPixel(x, y, new SKColor(grey, grey, grey, 255));
                    continue;
                }

                differing++;
                diff.SetPixel(x, y, new SKColor(0xB4, 0x23, 0x18, 255));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);

        using (var image = SKImage.FromBitmap(diff))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(diffPath))
        {
            data.SaveTo(stream);
        }

        var ratio = width * height == 0 ? 0 : (double)differing / (width * height);
        var sizeNote = left.Width == right.Width && left.Height == right.Height
            ? string.Empty
            : $" (compared over the {width}x{height} overlap)";

        return new DiffOutcome(true, ratio, $"{differing} of {width * height} pixels differ{sizeNote}");
    }

    private static bool Within(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) <= ChannelTolerance
        && Math.Abs(a.Green - b.Green) <= ChannelTolerance
        && Math.Abs(a.Blue - b.Blue) <= ChannelTolerance
        && Math.Abs(a.Alpha - b.Alpha) <= ChannelTolerance;
}
