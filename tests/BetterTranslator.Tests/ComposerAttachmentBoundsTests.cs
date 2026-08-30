using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.App.Views;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The composer stays where it is put, however many files are attached.
///
/// It did not. The chip strip was an unbounded Auto row at the top of the
/// composer, so it grew a row per pair of files: the history gave up its space
/// first and then had none left, after which the composer went on growing past
/// the bottom of the window and took the input and the send button with it. A
/// hundred files measured a composer 1718 units tall inside a 588 unit pane, so
/// the field a reader types into sat about a thousand units below the edge of
/// the screen.
///
/// Everything here is measured off the interactive desktop: the view is laid
/// out on an STA thread with no window and no HWND, and the figures are written
/// to a log beside the assertions.
/// </summary>
public sealed class ComposerAttachmentBoundsTests
{
    private const double WindowMinWidth = 880;
    private const double WindowMinHeight = 620;
    private const double SidebarWidth = 246;
    private const double CaptionHeight = 32;

    private static readonly double[] Scales = [1.0, 1.5, 2.0];

    private static readonly int[] Counts = [0, 1, 5, 20, 100, 200];

    private sealed record Measured
    {
        public required int Attachments { get; init; }
        public required double Scale { get; init; }
        public required double Strip { get; init; }
        public required double Scroller { get; init; }
        public required double Scrollable { get; init; }
        public required double Input { get; init; }
        public required double InputBottom { get; init; }
        public required double Composer { get; init; }
        public required double ComposerBottom { get; init; }
        public required double History { get; init; }
        public required double PaneHeight { get; init; }
        public required string Summary { get; init; }
        public required bool SummaryOutsideScroller { get; init; }
        public required double MinHeightToken { get; init; }
        public required double CapToken { get; init; }
        public required bool DpiApplied { get; init; }
    }

    private static readonly Lazy<IReadOnlyList<Measured>> Layouts = new(Measure, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void TheInputKeepsItsMinimumHeightAtEveryAttachmentCount()
    {
        foreach (var row in Layouts.Value)
        {
            row.Input.Should().BeGreaterThanOrEqualTo(
                row.MinHeightToken,
                $"the field is typed into, so it keeps its floor at {row.Attachments} attachments and {row.Scale * 100:F0} percent scaling");

            // Kept its height and lost the screen is the defect exactly: the
            // old strip left the input measuring its 42 units around y=1620 in
            // a pane 588 tall, so the floor held and nothing could be typed.
            row.InputBottom.Should().BeLessThanOrEqualTo(
                row.PaneHeight,
                $"a field of the right height below the window edge is still a field nobody can reach at {row.Attachments} attachments");
        }
    }

    [Fact]
    public void TheInputStaysInsideTheWindowAtEveryAttachmentCount()
    {
        // The defect, stated as a measurement. At a hundred attachments the
        // input used to be arranged around 1642, roughly a thousand units below
        // the bottom edge of a 588 unit pane.
        foreach (var row in Layouts.Value)
        {
            row.InputBottom.Should().BeLessThanOrEqualTo(
                row.PaneHeight,
                $"the field is below the strip, so it goes first: {row.Attachments} attachments at {row.Scale * 100:F0} percent");

            row.ComposerBottom.Should().BeLessThanOrEqualTo(
                row.PaneHeight,
                "the send button sits under the input and follows it out");
        }
    }

    [Fact]
    public void TheStripScrollsInsideAFixedBoundInsteadOfGrowing()
    {
        foreach (var row in Layouts.Value.Where(r => r.Attachments > 0))
        {
            row.Scroller.Should().BeLessThanOrEqualTo(
                row.CapToken + 0.5,
                "the strip is capped in device independent units, whatever the display scale");
        }

        // Past the cap the chips are still reachable, which is what makes the
        // cap a bound rather than a limit on what may be attached.
        foreach (var row in Layouts.Value.Where(r => r.Attachments >= 20))
        {
            row.Scrollable.Should().BeGreaterThan(
                0,
                "everything beyond the visible rows has to stay reachable by scrolling");
        }
    }

    [Fact]
    public void TheHistorySurrendersItsSpaceBeforeTheComposerDoes()
    {
        var scale = Layouts.Value.Where(r => r.Scale == 1.0).ToList();

        var empty = scale.Single(r => r.Attachments == 0);
        var loaded = scale.Single(r => r.Attachments == 100);

        loaded.History.Should().BeLessThan(empty.History, "the chips cost the list, not the composer");
        loaded.History.Should().BeGreaterThan(0, "the list is squeezed, not evicted");

        // And the composer stops growing rather than eating the rest.
        var saturated = scale.Where(r => r.Attachments >= 20).Select(r => r.Composer).ToList();
        (saturated.Max() - saturated.Min()).Should().BeLessThan(
            1,
            "past the cap the composer is the same height whether twenty files are attached or two hundred");
    }

    [Fact]
    public void TheTotalCountIsReadableWithoutScrolling()
    {
        foreach (var row in Layouts.Value.Where(r => r.Attachments > 0))
        {
            row.Summary.Should().Contain(
                row.Attachments.ToString(),
                "the strip shows three rows of a hundred, so the number has to be said");

            row.SummaryOutsideScroller.Should().BeTrue("a count inside the scroller would have to be scrolled to");
        }
    }

    [Fact]
    public void TheBoundHoldsAtEveryDisplayScale()
    {
        // Layout is in device independent units, so the cap cannot be a pixel
        // count that shrinks under scaling. What does move between 100, 150 and
        // 200 percent is layout rounding, which snaps an edge to the physical
        // pixel grid and can shift a measurement by a fraction of a unit. The
        // invariant is therefore that the composer measures the same to within
        // one unit at every scale, not that the doubles are equal.
        Layouts.Value.Select(r => r.DpiApplied).Should().AllSatisfy(
            applied => applied.Should().BeTrue("the measurement is worthless if the scale never reached the tree"));

        foreach (var count in Counts)
        {
            var heights = Layouts.Value
                .Where(r => r.Attachments == count)
                .Select(r => r.Composer)
                .ToList();

            (heights.Max() - heights.Min()).Should().BeLessThan(
                1,
                $"the composer is bounded in device independent units with {count} attachments");
        }

        foreach (var scale in Scales)
        {
            var loaded = Layouts.Value.Single(r => r.Scale == scale && r.Attachments == 100);

            loaded.Scroller.Should().BeLessThanOrEqualTo(loaded.CapToken + 0.5);
            loaded.ComposerBottom.Should().BeLessThanOrEqualTo(loaded.PaneHeight);
            loaded.Input.Should().BeGreaterThanOrEqualTo(loaded.MinHeightToken);
        }
    }

    private static IReadOnlyList<Measured> Measure()
    {
        var log = new StringBuilder();
        var rows = new List<Measured>();

        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var minHeight = (double)Application.Current!.Resources["ComposerMinHeight"];
            var cap = (double)Application.Current.Resources["ComposerAttachmentsMaxHeight"];

            var root = Path.Combine(Path.GetTempPath(), "bt-composer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var database = new Database(new AppPaths(root));
            database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

            var workspace = new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null);
            var view = new ChatView { DataContext = workspace };

            var pane = new Border
            {
                Child = view,
                UseLayoutRounding = true,
                Width = WindowMinWidth - SidebarWidth,
                Height = WindowMinHeight - CaptionHeight,
            };

            foreach (var scale in Scales)
            {
                var applied = TrySetDpi(pane, scale);

                log.AppendLine($"=== display scaling {scale * 100:F0} percent, pane {pane.Width:F0} x {pane.Height:F0} DIU, root dpi applied {applied} ===");
                log.AppendLine("attachments   strip  scroller  scrollable    input  inputBottom  composer  composerBottom  history     pane");

                foreach (var count in Counts)
                {
                    Fill(workspace, count, root);

                    pane.Measure(new Size(pane.Width, pane.Height));
                    pane.Arrange(new Rect(0, 0, pane.Width, pane.Height));
                    pane.UpdateLayout();

                    var measured = Read(view, pane, count, scale, minHeight, cap, applied);

                    rows.Add(measured);
                    log.AppendLine(Format(measured));
                }

                log.AppendLine(string.Empty);
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }

            return null;
        }));

        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "bt-composer-attachment-bounds.log"),
            log.ToString() + (failure ?? "no failure") + Environment.NewLine);

        failure.Should().BeNull();
        rows.Should().HaveCount(Scales.Length * Counts.Length);

        return rows;
    }

    private static void Fill(ChatWorkspaceViewModel workspace, int count, string root)
    {
        while (workspace.Attachments.Count > count)
        {
            workspace.Attachments.RemoveAt(workspace.Attachments.Count - 1);
        }

        while (workspace.Attachments.Count < count)
        {
            workspace.Attachments.Add(AttachmentViewModel.File(
                Path.Combine(root, $"handbook-{workspace.Attachments.Count:D3}.md")));
        }
    }

    private static Measured Read(
        ChatView view,
        FrameworkElement pane,
        int count,
        double scale,
        double minHeight,
        double cap,
        bool dpiApplied)
    {
        var strip = (ItemsControl)view.FindName("AttachmentStrip");
        var input = (TextBox)view.FindName("ComposerInput");
        var scroller = Ancestor<ScrollViewer>(strip);
        var composer = Ancestor<Border>(input);
        var history = Visuals<ScrollViewer>(view).FirstOrDefault(s => s != scroller);

        var summary = Visuals<TextBlock>(view)
            .FirstOrDefault(t => System.Windows.Data.BindingOperations
                .GetBinding(t, TextBlock.TextProperty)?.Path.Path == nameof(ChatWorkspaceViewModel.AttachmentSummary));

        // A collapsed element is never measured, so it keeps whatever it last
        // arranged at. Reading that back would report a strip that is not on
        // screen as having a height.
        var showing = scroller?.Visibility == Visibility.Visible;

        return new Measured
        {
            Attachments = count,
            Scale = scale,
            Strip = showing ? strip!.ActualHeight : 0,
            Scroller = showing ? scroller!.ActualHeight : 0,
            Scrollable = showing ? scroller!.ScrollableHeight : 0,
            Input = input?.ActualHeight ?? 0,
            InputBottom = Bottom(input, pane),
            Composer = composer?.ActualHeight ?? 0,
            ComposerBottom = Bottom(composer, pane),
            History = history?.ActualHeight ?? 0,
            PaneHeight = pane.Height,
            Summary = summary?.Text ?? string.Empty,
            SummaryOutsideScroller = summary is not null && Ancestor<ScrollViewer>(summary) != scroller,
            MinHeightToken = minHeight,
            CapToken = cap,
            DpiApplied = dpiApplied,
        };
    }

    /// <summary>
    /// Where the bottom edge of a control lands inside the pane. IsVisible is
    /// deliberately not consulted: nothing here is connected to a presentation
    /// source, so it is false for every element on this thread and would report
    /// a laid out control as absent.
    /// </summary>
    private static double Bottom(FrameworkElement? element, FrameworkElement pane) =>
        element is null || element.Visibility != Visibility.Visible
            ? double.NaN
            : element.TransformToAncestor(pane).Transform(new Point(0, element.ActualHeight)).Y;

    private static string Format(Measured row) => string.Join(
        "  ",
        row.Attachments.ToString().PadLeft(11),
        row.Strip.ToString("F1").PadLeft(6),
        row.Scroller.ToString("F1").PadLeft(8),
        row.Scrollable.ToString("F1").PadLeft(10),
        row.Input.ToString("F1").PadLeft(7),
        row.InputBottom.ToString("F1").PadLeft(11),
        row.Composer.ToString("F1").PadLeft(8),
        row.ComposerBottom.ToString("F1").PadLeft(14),
        row.History.ToString("F1").PadLeft(7),
        row.PaneHeight.ToString("F1").PadLeft(8));

    private static bool TrySetDpi(Visual visual, double scale)
    {
        try
        {
            VisualTreeHelper.SetRootDpi(visual, new DpiScale(scale, scale));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static T? Ancestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        var current = node is null ? null : VisualTreeHelper.GetParent(node);

        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as T;
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Visuals<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Restored on the way out. Application.Current is process wide, and leaving
    /// these merged changes what unrelated tests see when they ask the token
    /// store a question.
    /// </summary>
    private static string? WithTokens(Func<string?> body)
    {
        var added = new List<ResourceDictionary>();

        foreach (var name in Dictionaries)
        {
            var source = new Uri($"pack://application:,,,/BetterTranslator;component/{name}.xaml", UriKind.Absolute);

            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != source))
            {
                var dictionary = new ResourceDictionary { Source = source };

                Application.Current.Resources.MergedDictionaries.Add(dictionary);
                added.Add(dictionary);
            }
        }

        try
        {
            return body();
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
        finally
        {
            foreach (var dictionary in added)
            {
                Application.Current!.Resources.MergedDictionaries.Remove(dictionary);
            }
        }
    }

    private static readonly string[] Dictionaries =
    [
        "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
        "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
        "Verification/VerificationTokens",
    ];
}
