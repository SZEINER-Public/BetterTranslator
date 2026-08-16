using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using BetterTranslator.App.Controls;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class AppIconTests
{
    private static readonly int[] RequiredFrames = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private const string IconRelativePath = @"src\BetterTranslator.App\Assets\app-icon.ico";
    private const string MarkRelativePath = @"src\BetterTranslator.App\Assets\app-icon.png";
    private const string IconPackUri = "pack://application:,,,/BetterTranslator;component/Assets/app-icon.ico";

    [Fact]
    public void IconCarriesEveryRequiredFrame()
    {
        var entries = ReadIconDirectory(File.ReadAllBytes(RepoFile(IconRelativePath)));

        entries.Select(entry => entry.Width).Should().Equal(RequiredFrames);
        entries.Should().OnlyContain(entry => entry.Width == entry.Height);
        entries.Should().OnlyContain(entry => entry.BitCount == 32);
        entries.Single(entry => entry.Width == 256).IsPng.Should().BeTrue();
    }

    [Fact]
    public void ExecutableProjectPointsAtTheIcon()
    {
        var project = XDocument.Load(RepoFile(@"src\BetterTranslator.App\BetterTranslator.App.csproj"));

        var applicationIcon = project
            .Descendants("ApplicationIcon")
            .Select(element => element.Value.Trim())
            .SingleOrDefault();

        applicationIcon.Should().Be(@"Assets\app-icon.ico");

        project
            .Descendants("Resource")
            .Select(element => element.Attribute("Include")?.Value)
            .Should().Contain(@"Assets\app-icon.ico");
    }

    [Fact]
    public void WindowIconResolvesToAnImage()
    {
        var declared = Regex.Match(
            File.ReadAllText(RepoFile(@"src\BetterTranslator.App\MainWindow.xaml")),
            "\\bIcon=\"(pack://[^\"]+)\"");

        declared.Success.Should().BeTrue("MainWindow declares the icon Windows draws for the running window");
        declared.Groups[1].Value.Should().Be(IconPackUri);

        var resolved = StaRunner.Run(() =>
        {
            var icon = new ImageSourceConverter().ConvertFromString(declared.Groups[1].Value) as ImageSource;

            var frames = icon is BitmapFrame bitmap
                ? bitmap.Decoder.Frames.Select(frame => frame.PixelWidth).Order().ToList()
                : [];

            return (Resolved: icon is not null, Frames: frames);
        });

        resolved.Resolved.Should().BeTrue("MainWindow.Icon has to produce an image, not a null");
        resolved.Frames.Should().Equal(RequiredFrames);
    }

    [Fact]
    public void TheTreeCarriesOneIconAndOneMaster()
    {
        var root = RepoRoot();

        var assets = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(root, path))
            .Where(IsMarkAsset)
            .Select(path => Path.GetRelativePath(root, path))
            .Order()
            .ToList();

        assets.Should().Equal(IconRelativePath, MarkRelativePath);
    }

    [Fact]
    public void TheInAppMarkMatchesTheIconSource()
    {
        var master = StaRunner.Run(() => MeasureMaster(RepoFile(MarkRelativePath)));

        master.Bars.Should().HaveCount(6);

        var accent = master.Bars
            .GroupBy(bar => bar.Colour)
            .OrderBy(group => group.Count())
            .First()
            .Key;

        var dark = master.Bars.Where(bar => bar.Colour != accent).Select(Relative).ToList();
        var accented = master.Bars.Where(bar => bar.Colour == accent).Select(Relative).ToList();

        Wordmark.DarkBars.Should().Equal(dark);
        Wordmark.AccentBars.Should().Equal(accented);

        Wordmark.DesignWidth.Should().Be(dark.Concat(accented).Max(bar => bar.X + bar.W));
        Wordmark.DesignHeight.Should().Be(dark.Concat(accented).Max(bar => bar.Y + bar.H));

        var brushes = StaRunner.Run(() =>
        {
            var themes = new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/BetterTranslator;component/Themes/Colors.xaml", UriKind.Absolute),
            };

            return new[] { "BrushText", "BrushAccent" }
                .Select(key => ((SolidColorBrush)themes[key]).Color.ToString(CultureInfo.InvariantCulture))
                .ToList();
        });

        var darkColour = master.Bars.First(bar => bar.Colour != accent).Colour;

        brushes[0].Should().Be($"#{darkColour:X8}");
        brushes[1].Should().Be($"#{accent:X8}");

        (double X, double Y, double W, double H) Relative(MasterBar bar) =>
            (bar.X - master.OriginX, bar.Y - master.OriginY, bar.W, bar.H);
    }

    private readonly record struct MasterBar(double X, double Y, double W, double H, uint Colour);

    private readonly record struct Master(int Size, double OriginX, double OriginY, IReadOnlyList<MasterBar> Bars);

    private static Master MeasureMaster(string path)
    {
        var frame = BitmapFrame.Create(
            new Uri(path),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        frame.PixelWidth.Should().Be(frame.PixelHeight, "the master mark has to be square");
        frame.PixelWidth.Should().BeGreaterThanOrEqualTo(256, "the master mark has to be at least 256 pixels");

        var size = frame.PixelWidth;
        var pixels = new byte[size * size * 4];
        new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);

        uint At(int x, int y)
        {
            var offset = ((y * size) + x) * 4;
            return ((uint)pixels[offset + 3] << 24)
                | ((uint)pixels[offset + 2] << 16)
                | ((uint)pixels[offset + 1] << 8)
                | pixels[offset];
        }

        var edge = At(size / 2, 0);
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

            return Math.Abs((int)((colour >> 16) & 0xFF) - (int)((ground >> 16) & 0xFF))
                + Math.Abs((int)((colour >> 8) & 0xFF) - (int)((ground >> 8) & 0xFF))
                + Math.Abs((int)(colour & 0xFF) - (int)(ground & 0xFF)) > 60;
        }

        var bars = new List<MasterBar>();
        foreach (var (top, bottom) in Runs(y => Enumerable.Range(0, size).Any(x => IsInk(At(x, y))), size))
        {
            var scan = (top + bottom) / 2;
            foreach (var (left, right) in Runs(x => IsInk(At(x, scan)), size))
            {
                bars.Add(new MasterBar(left, top, right - left + 1, bottom - top + 1, At((left + right) / 2, scan)));
            }
        }

        return new Master(size, bars.Min(bar => bar.X), bars.Min(bar => bar.Y), bars);
    }

    private static IEnumerable<(int Start, int End)> Runs(Func<int, bool> occupied, int length)
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

    [Fact]
    public void EveryProducedExecutableCarriesTheSameFrames()
    {
        var stamped = File.GetLastWriteTimeUtc(RepoFile(IconRelativePath));

        var produced = Executables()
            .Where(path => File.GetLastWriteTimeUtc(path) >= stamped)
            .ToList();

        produced.Should().NotBeEmpty("the icon group has to be read out of a real build of the app");

        foreach (var executable in produced)
        {
            var module = LoadLibraryEx(executable, IntPtr.Zero, LoadLibraryAsDataFile);
            module.Should().NotBe(IntPtr.Zero, "{0} has to be readable as a resource module", executable);

            try
            {
                GroupIconFrames(module).Should().Equal(RequiredFrames, "{0} carries the icon Windows draws", executable);
            }
            finally
            {
                FreeLibrary(module);
            }
        }
    }

    private static IEnumerable<string> Executables()
    {
        var output = Path.Combine(RepoRoot(), "src", "BetterTranslator.App", "bin");

        return Directory.Exists(output)
            ? Directory.EnumerateFiles(output, "BetterTranslator.exe", SearchOption.AllDirectories)
            : [];
    }

    private static List<int> GroupIconFrames(IntPtr module)
    {
        var names = new List<IntPtr>();
        EnumResourceNames(module, RtGroupIcon, (_, _, name, _) =>
        {
            names.Add(name);
            return true;
        }, IntPtr.Zero).Should().BeTrue();

        names.Should().ContainSingle("the executable carries one icon group");

        var info = FindResource(module, names[0], RtGroupIcon);
        var handle = LoadResource(module, info);
        var address = LockResource(handle);
        var length = SizeofResource(module, info);

        var directory = new byte[length];
        Marshal.Copy(address, directory, 0, (int)length);

        var count = BitConverter.ToUInt16(directory, 4);
        var frames = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var width = directory[6 + (i * 14)];
            frames.Add(width == 0 ? 256 : width);
        }

        frames.Sort();
        return frames;
    }

    private static IReadOnlyList<(int Width, int Height, int BitCount, bool IsPng)> ReadIconDirectory(byte[] icon)
    {
        BitConverter.ToUInt16(icon, 0).Should().Be(0);
        BitConverter.ToUInt16(icon, 2).Should().Be(1);

        var count = BitConverter.ToUInt16(icon, 4);
        var entries = new List<(int, int, int, bool)>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            var width = icon[entry] == 0 ? 256 : icon[entry];
            var height = icon[entry + 1] == 0 ? 256 : icon[entry + 1];
            var bitCount = BitConverter.ToUInt16(icon, entry + 6);
            var offset = BitConverter.ToInt32(icon, entry + 12);
            var isPng = icon[offset] == 0x89 && icon[offset + 1] == 0x50;

            entries.Add((width, height, bitCount, isPng));
        }

        return entries;
    }

    private static readonly string[] ImageExtensions =
        [".ico", ".svg", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".icns", ".webp"];

    private static bool IsMarkAsset(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension))
        {
            return false;
        }

        if (extension == ".ico")
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.StartsWith("app-icon", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("wordmark", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            segment is "bin" or "obj" or ".git" or ".vs" or "node_modules" or "tools");
    }

    private static string RepoFile(string relative) =>
        Path.Combine(RepoRoot(), relative);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BetterTranslator.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");
        return directory!.FullName;
    }

    private const uint LoadLibraryAsDataFile = 0x00000002;

    private static readonly IntPtr RtGroupIcon = new(14);

    private delegate bool EnumResourceNamesCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(
        IntPtr module,
        IntPtr type,
        EnumResourceNamesCallback callback,
        IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr info);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr info);
}
