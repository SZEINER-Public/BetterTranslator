using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BetterTranslator.App.Controls;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Tests.Performance;

public sealed record FrameReport(IReadOnlyList<double> Frames, int Steps, int SelectedCharacters)
{
    public double P50 => Percentile(50);

    public double P95 => Percentile(95);

    public int OverBudget => Frames.Count(f => f > 16.7);

    public double Worst => Frames.Count == 0 ? 0 : Frames.Max();

    private double Percentile(int percent)
    {
        if (Frames.Count == 0)
        {
            return 0;
        }

        var ordered = Frames.Order().ToList();
        var rank = (int)Math.Ceiling(percent / 100d * ordered.Count) - 1;

        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }

    public override string ToString() => string.Join(
        "  ",
        $"frames {Frames.Count,4}",
        $"p50 {P50,6:F2} ms",
        $"p95 {P95,6:F2} ms",
        $"over 16.7 {OverBudget,4}",
        $"worst {Worst,7:F2} ms",
        $"steps {Steps}",
        $"chars {SelectedCharacters}");
}

/// <summary>
/// Frame times for a scripted selection drag, captured from a real render loop.
///
/// It needs a composition target, which needs a window, so it makes one that no
/// reader can see or be interrupted by: an HwndSource placed far outside any
/// monitor and marked WS_EX_NOACTIVATE, so it never takes focus and never
/// appears on the interactive desktop. It is destroyed with the thread.
///
/// The drag is driven through the selection API rather than by moving the mouse.
/// What is being measured is the work a growing selection causes, and a real
/// pointer would add the input stack's own latency to every sample without
/// telling us anything about the text.
/// </summary>
public static class FrameTimingHarness
{
    private const int OffScreen = -32000;

    private const int WsPopup = unchecked((int)0x80000000);

    private const int WsExNoActivate = 0x08000000;

    private const int WsExToolWindow = 0x00000080;

    public static FrameReport DragAcross(string markdown, int stepCharacters, int steps, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        FrameReport? report = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                report = Run(markdown, stepCharacters, steps, width, height);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("The frame timing run failed: " + failure.Message, failure);
        }

        return report!;
    }

    private static FrameReport Run(string markdown, int stepCharacters, int steps, int width, int height)
    {
        if (Application.Current is null)
        {
            _ = new Application();
        }

        Themes();

        var text = new RichTextBox
        {
            Document = MarkdownFlow.Build(MarkdownBlockParser.Parse(markdown)),
            IsReadOnly = true,
            Width = width,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var host = new Border { Width = width, Child = text };

        using var source = new HwndSource(new HwndSourceParameters("frame timing")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExNoActivate | WsExToolWindow,
            PositionX = OffScreen,
            PositionY = OffScreen,
            Width = width,
            Height = height,
            UsesPerPixelOpacity = false,
        })
        {
            RootVisual = host,
        };

        var frames = new List<double>();
        var clock = Stopwatch.StartNew();
        var last = default(double?);
        var sampling = false;

        void OnFrame(object? sender, EventArgs e)
        {
            var now = clock.Elapsed.TotalMilliseconds;

            if (sampling && last is { } previous)
            {
                frames.Add(now - previous);
            }

            last = now;
        }

        CompositionTarget.Rendering += OnFrame;

        var document = text.Document;
        var start = document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward) ?? document.ContentStart;
        var end = start;
        var taken = 0;
        var selected = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };

        timer.Tick += (_, _) =>
        {
            if (taken >= steps)
            {
                timer.Stop();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                return;
            }

            end = end.GetPositionAtOffset(stepCharacters, LogicalDirection.Forward) ?? document.ContentEnd;

            text.Selection.Select(start, end);
            selected = new TextRange(start, end).Text.Length;
            taken++;
        };

        // Laid out and given one frame before anything is sampled, so the first
        // measurement is a drag frame rather than the cost of the first paint.
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        sampling = true;
        timer.Start();

        Dispatcher.Run();

        CompositionTarget.Rendering -= OnFrame;

        return new FrameReport(frames, taken, selected);
    }

    private static void Themes()
    {
        string[] names =
        [
            "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
            "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
        ];

        foreach (var name in names)
        {
            var source = new Uri($"pack://application:,,,/BetterTranslator;component/{name}.xaml", UriKind.Absolute);

            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != source))
            {
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = source });
            }
        }
    }

    public static string LongDocument(int sections)
    {
        var text = new StringBuilder();

        for (var i = 0; i < sections; i++)
        {
            text.Append("## Section ").Append(i).Append("\n\n")
                .Append("A paragraph with `code`, **bold** and a [link](https://example.com/")
                .Append(i)
                .Append(") long enough to wrap across the reading column more than once.\n\n");
        }

        return text.ToString();
    }
}
