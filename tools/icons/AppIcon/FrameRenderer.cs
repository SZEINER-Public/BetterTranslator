namespace BetterTranslator.Tools.AppIcon;

internal static class FrameRenderer
{
    private const int SamplesPerAxis = 4;

    public static byte[] Render(FrameLayout layout)
    {
        var pixels = new byte[layout.Size * layout.Size * 4];

        if (layout.HasGround)
        {
            PaintGround(pixels, layout.Size, layout.GroundRadius, layout.GroundColour);
        }

        foreach (var bar in layout.Bars)
        {
            PaintBar(pixels, layout.Size, bar);
        }

        return pixels;
    }

    private static void PaintGround(byte[] pixels, int size, double radius, uint colour)
    {
        var blue = (byte)(colour & 0xFF);
        var green = (byte)((colour >> 8) & 0xFF);
        var red = (byte)((colour >> 16) & 0xFF);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var coverage = Coverage(x, y, size, radius);
                if (coverage <= 0)
                {
                    continue;
                }

                var offset = ((y * size) + x) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = (byte)Math.Round(coverage * 255, MidpointRounding.AwayFromZero);
            }
        }
    }

    private static double Coverage(int x, int y, int size, double radius)
    {
        if (radius <= 0)
        {
            return 1;
        }

        var near =
            (x < radius || x >= size - radius) &&
            (y < radius || y >= size - radius);

        if (!near)
        {
            return 1;
        }

        var inside = 0;
        for (var sy = 0; sy < SamplesPerAxis; sy++)
        {
            for (var sx = 0; sx < SamplesPerAxis; sx++)
            {
                var px = x + ((sx + 0.5) / SamplesPerAxis);
                var py = y + ((sy + 0.5) / SamplesPerAxis);

                var cx = px < radius ? radius : px > size - radius ? size - radius : px;
                var cy = py < radius ? radius : py > size - radius ? size - radius : py;

                var dx = px - cx;
                var dy = py - cy;

                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    inside++;
                }
            }
        }

        return (double)inside / (SamplesPerAxis * SamplesPerAxis);
    }

    private static void PaintBar(byte[] pixels, int size, PlacedBar bar)
    {
        var blue = (byte)(bar.Colour & 0xFF);
        var green = (byte)((bar.Colour >> 8) & 0xFF);
        var red = (byte)((bar.Colour >> 16) & 0xFF);

        for (var y = bar.Y; y < bar.Y + bar.H; y++)
        {
            if (y < 0 || y >= size)
            {
                continue;
            }

            for (var x = bar.X; x < bar.X + bar.W; x++)
            {
                if (x < 0 || x >= size)
                {
                    continue;
                }

                var offset = ((y * size) + x) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 0xFF;
            }
        }
    }
}
