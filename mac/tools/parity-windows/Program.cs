using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterTranslator.Mac.Parity.Windows;

public sealed class NodeRecord
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("zIndex")]
    public int ZIndex { get; set; }

    [JsonPropertyName("bounds")]
    public double[] Bounds { get; set; } = [0, 0, 0, 0];

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("fontSize")]
    public double? FontSize { get; set; }

    [JsonPropertyName("foregroundToken")]
    public string? ForegroundToken { get; set; }

    [JsonPropertyName("backgroundToken")]
    public string? BackgroundToken { get; set; }

    [JsonPropertyName("children")]
    public List<NodeRecord> Children { get; set; } = [];
}

public sealed class TreeRecord
{
    [JsonPropertyName("screen")]
    public string Screen { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;

    [JsonPropertyName("head")]
    public string Head { get; set; } = "windows";

    [JsonPropertyName("root")]
    public NodeRecord Root { get; set; } = new();
}

public sealed record Screen(string Id, string State, Func<FrameworkElement> Build);

public static class Program
{
    private static readonly (int Width, int Height)[] Sizes = [(1440, 900), (1280, 800)];

    private static readonly Dictionary<uint, string> TokenByColor = [];

    [STAThread]
    public static int Main(string[] args)
    {
        var macFolder = MacFolder();
        var output = Path.Combine(macFolder, "parity-out");

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadThemes(application);
        IndexTokens(application);

        foreach (var screen in ScreenRegistry.All())
        {
            foreach (var (width, height) in Sizes)
            {
                var folder = Path.Combine(output, screen.Id, $"{width}x{height}");
                Directory.CreateDirectory(folder);

                var element = screen.Build();
                element.Measure(new Size(width, height));
                element.Arrange(new Rect(0, 0, width, height));
                element.UpdateLayout();

                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (var stream = File.Create(Path.Combine(folder, "windows.png")))
                {
                    encoder.Save(stream);
                }

                var tree = new TreeRecord
                {
                    Screen = screen.Id,
                    Size = $"{width}x{height}",
                    Root = Walk(element, 0),
                };

                File.WriteAllText(
                    Path.Combine(folder, "windows.tree.json"),
                    JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        application.Shutdown();

        return 0;
    }

    private static void LoadThemes(Application application)
    {
        string[] sources =
        [
            "Themes/Colors.xaml", "Themes/Typography.xaml", "Themes/Metrics.xaml", "Themes/Icons.xaml",
            "Themes/Flags.xaml", "Themes/Controls.xaml", "Themes/Markdown.xaml", "Themes/Json.xaml",
            "Verification/VerificationTokens.xaml",
        ];

        foreach (var source in sources)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/BetterTranslator.App.Capture;component/{source}", UriKind.Absolute),
            });
        }
    }

    private static void IndexTokens(Application application)
    {
        foreach (var key in TokenKeys.All)
        {
            if (application.TryFindResource(key) is SolidColorBrush brush)
            {
                var packed = ((uint)brush.Color.A << 24) | ((uint)brush.Color.R << 16) | ((uint)brush.Color.G << 8) | brush.Color.B;
                TokenByColor.TryAdd(packed, key);
            }
        }
    }

    private static NodeRecord Walk(DependencyObject visual, int order)
    {
        var element = visual as FrameworkElement;

        var node = new NodeRecord
        {
            Role = Role(visual),
            Order = order,
            ZIndex = element is null ? 0 : Panel.GetZIndex(element),
            Bounds = element is null
                ? [0, 0, 0, 0]
                :
                [
                    Round(GetLeft(element)), Round(GetTop(element)),
                    Round(element.ActualWidth), Round(element.ActualHeight),
                ],
            Text = Text(visual),
            FontSize = FontSizeOf(visual),
            ForegroundToken = Token(Foreground(visual)),
            BackgroundToken = Token(Background(visual)),
        };

        var count = VisualTreeHelper.GetChildrenCount(visual);

        for (var i = 0; i < count; i++)
        {
            node.Children.Add(Walk(VisualTreeHelper.GetChild(visual, i), i));
        }

        return node;
    }

    private static double GetLeft(FrameworkElement element) =>
        VisualTreeHelper.GetOffset(element).X;

    private static double GetTop(FrameworkElement element) =>
        VisualTreeHelper.GetOffset(element).Y;

    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;

    private static string Role(DependencyObject visual) =>
        visual is FrameworkElement { Name.Length: > 0 } named
            ? visual.GetType().Name + "#" + named.Name
            : visual.GetType().Name;

    private static string? Text(DependencyObject visual) => visual switch
    {
        TextBlock block => block.Text,
        _ => null,
    };

    private static double? FontSizeOf(DependencyObject visual) => visual switch
    {
        TextBlock block => block.FontSize,
        Control control => control.FontSize,
        _ => null,
    };

    private static Brush? Foreground(DependencyObject visual) => visual switch
    {
        TextBlock block => block.Foreground,
        Control control => control.Foreground,
        System.Windows.Shapes.Shape shape => shape.Fill,
        _ => null,
    };

    private static Brush? Background(DependencyObject visual) => visual switch
    {
        Border border => border.Background,
        Panel panel => panel.Background,
        Control control => control.Background,
        _ => null,
    };

    private static string? Token(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush is null ? null : "unnamed:" + brush.GetType().Name;
        }

        var packed = ((uint)solid.Color.A << 24) | ((uint)solid.Color.R << 16) | ((uint)solid.Color.G << 8) | solid.Color.B;

        return TokenByColor.TryGetValue(packed, out var key)
            ? key
            : "unnamed:" + solid.Color.ToString(CultureInfo.InvariantCulture);
    }

    private static string MacFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.Name != "mac")
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The mac folder could not be located from " + AppContext.BaseDirectory + ".");
    }
}
