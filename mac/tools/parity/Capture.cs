using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BetterTranslator.Mac.Parity;

public sealed record CaptureResult(TreeRecord Tree, string PngPath);

public static class Capture
{
    public static CaptureResult Render(ScreenDefinition screen, LogicalSize size, TokenIndex tokens, string outputFolder)
    {
        var content = screen.Build();

        var window = new Window
        {
            Width = size.Width,
            Height = size.Height,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Content = content,
        };

        window.Show();

        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(size.Width, size.Height));
        window.Arrange(new Rect(0, 0, size.Width, size.Height));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Directory.CreateDirectory(outputFolder);
        var pngPath = System.IO.Path.Combine(outputFolder, "mac.png");

        var frame = window.CaptureRenderedFrame();
        frame?.Save(pngPath);

        var tree = new TreeRecord
        {
            Screen = screen.Id,
            Size = size.Id,
            Head = "mac",
            Root = Walk(window, tokens, 0),
        };

        window.Close();

        return new CaptureResult(tree, pngPath);
    }

    private static NodeRecord Walk(Visual visual, TokenIndex tokens, int order)
    {
        var node = new NodeRecord
        {
            Role = Role(visual),
            Order = order,
            ZIndex = visual.ZIndex,
            Bounds = [Round(visual.Bounds.X), Round(visual.Bounds.Y), Round(visual.Bounds.Width), Round(visual.Bounds.Height)],
            Text = Text(visual),
            FontSize = FontSizeOf(visual),
            ForegroundToken = tokens.Resolve(Foreground(visual)),
            BackgroundToken = tokens.Resolve(Background(visual)),
        };

        var index = 0;

        foreach (var child in visual.GetVisualChildren())
        {
            node.Children.Add(Walk(child, tokens, index));
            index++;
        }

        return node;
    }

    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;

    private static string Role(Visual visual) =>
        visual is Control { Name: { Length: > 0 } name }
            ? visual.GetType().Name + "#" + name
            : visual.GetType().Name;

    private static string? Text(Visual visual) => visual switch
    {
        TextBlock block => block.Text,
        TextPresenter presenter => presenter.Text,
        _ => null,
    };

    private static double? FontSizeOf(Visual visual)
    {
        var size = visual is TextBlock block ? block.FontSize
            : visual is TemplatedControl control ? control.FontSize
            : (double?)null;

        return size is { } value && double.IsFinite(value) ? value : null;
    }

    private static IBrush? Foreground(Visual visual) => visual switch
    {
        TextBlock block => block.Foreground,
        TemplatedControl control => control.Foreground,
        Shape shape => shape.Fill,
        _ => null,
    };

    private static IBrush? Background(Visual visual) => visual switch
    {
        Border border => border.Background,
        Panel panel => panel.Background,
        TemplatedControl control => control.Background,
        _ => null,
    };
}
