using System.Globalization;
using System.IO;
using BetterTranslator.Tools.AppIcon;

var source = args.Length > 0
    ? args[0]
    : System.IO.Path.Combine("src", "BetterTranslator.App", "Assets", "app-icon.png");

var target = args.Length > 1
    ? args[1]
    : System.IO.Path.ChangeExtension(source, ".ico");

if (!File.Exists(source))
{
    Console.Error.WriteLine($"Master mark not found: {System.IO.Path.GetFullPath(source)}");
    return 1;
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
int[] simplified = [16, 20];

var mark = MarkSource.Load(source);

Console.WriteLine(string.Format(
    CultureInfo.InvariantCulture,
    "master {0} px, mark {1}x{2} at {3},{4}, {5} bars, accent #{6:X6}, ground {7}",
    mark.MasterSize,
    mark.MarkWidth,
    mark.MarkHeight,
    mark.MarkX,
    mark.MarkY,
    mark.Bars.Count,
    mark.AccentColour & 0xFFFFFF,
    mark.HasGround ? $"#{mark.GroundColour & 0xFFFFFF:X6} radius {mark.GroundRadius}" : "transparent"));

var frames = new List<(int Size, byte[] Pixels)>(sizes.Length);
foreach (var size in sizes)
{
    var layout = FrameLayout.For(mark, size, simplified.Contains(size));
    frames.Add((size, FrameRenderer.Render(layout)));

    var first = layout.Bars[0];
    var width = layout.Bars.Max(bar => bar.X + bar.W) - layout.Bars.Min(bar => bar.X);
    var height = layout.Bars.Max(bar => bar.Y + bar.H) - layout.Bars.Min(bar => bar.Y);

    Console.WriteLine(string.Format(
        CultureInfo.InvariantCulture,
        "{0,3} px  bars {1}  bar {2}x{3}  mark {4}x{5}  {6}",
        size,
        layout.Bars.Count,
        first.W,
        first.H,
        width,
        height,
        layout.AccentRowOnly ? "accent row" : "full mark"));
}

Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target))!);
IconWriter.Write(target, frames);

Console.WriteLine($"Wrote {System.IO.Path.GetFullPath(target)} ({new FileInfo(target).Length} bytes)");
return 0;
