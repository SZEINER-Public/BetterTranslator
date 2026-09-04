namespace BetterTranslator.Mac.Parity;

public readonly record struct LogicalSize(int Width, int Height)
{
    public string Id => $"{Width}x{Height}";

    public static readonly LogicalSize[] All =
    [
        new(1440, 900),
        new(1280, 800),
    ];
}
