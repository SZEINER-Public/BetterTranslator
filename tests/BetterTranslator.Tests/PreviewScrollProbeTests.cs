using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

public sealed record ContentAreaMeasurement(
    double ViewportWidth,
    double ViewportHeight,
    double ExtentWidth,
    double ExtentHeight,
    double ScrollableHeight,
    bool HasVisibleSelectableSurface)
{
    public bool CanScrollVertically => ScrollableHeight > 0;

    public bool WrapsToViewport => ExtentWidth <= ViewportWidth + 1;

    public override string ToString() => string.Join(
        "  ",
        $"viewport {ViewportWidth:F0}x{ViewportHeight:F0}",
        $"extent {ExtentWidth:F0}x{ExtentHeight:F0}",
        $"scrollableY {ScrollableHeight:F0}",
        $"wraps {WrapsToViewport}",
        $"selectable {HasVisibleSelectableSurface}");
}

/// <summary>
/// The preview's content area, measured offscreen.
///
/// Both failures this locks down were invisible to every other test and obvious
/// the moment the panel was used. An Auto horizontal scrollbar measures the
/// content with infinite width, so every wrapping block laid out on one line and
/// the document measured 58779 x 94: the wheel had nothing to scroll because the
/// content was shorter than the viewport. And every text surface was a TextBlock,
/// which WPF cannot select, so nothing in a translated file could be copied out.
/// </summary>
/// <summary>
/// Both views are measured on ONE STA thread. WPF resources are Freezables owned
/// by the thread that made them, and the Application the runner reuses is static,
/// so a second thread measuring against the first one's dictionaries throws.
/// </summary>
public sealed class PreviewContentAreaFixture
{
    public PreviewContentAreaFixture()
    {
        var measured = PreviewScrollProbeTests.MeasureBoth();

        Formatted = measured.Formatted;
        SourceText = measured.SourceText;
        EveryBlockKind = measured.EveryBlockKind;
    }

    public ContentAreaMeasurement Formatted { get; }

    public ContentAreaMeasurement SourceText { get; }

    /// <summary>One rendered block name per Markdown construct, in document order.</summary>
    public IReadOnlyList<string> EveryBlockKind { get; }
}

public sealed class PreviewScrollProbeTests(PreviewContentAreaFixture fixture, ITestOutputHelper output)
    : IClassFixture<PreviewContentAreaFixture>
{
    [Fact]
    public void TheFormattedViewWrapsAndScrolls()
    {
        output.WriteLine("formatted   " + fixture.Formatted);

        fixture.Formatted.WrapsToViewport.Should().BeTrue(
            "an Auto horizontal scrollbar measures with infinite width and stops every block wrapping");
        fixture.Formatted.CanScrollVertically.Should().BeTrue(
            "a document taller than the panel must scroll under the wheel");
        fixture.Formatted.HasVisibleSelectableSurface.Should().BeTrue(
            "the rendered view is a FlowDocument so a reader can select across its blocks");
    }

    [Fact]
    public void EveryMarkdownConstructRendersIntoTheDocument()
    {
        output.WriteLine(string.Join(", ", fixture.EveryBlockKind));

        fixture.EveryBlockKind.Should().ContainInOrder("Paragraph", "Paragraph", "Paragraph", "Table", "Section", "BlockUIContainer");
    }

    [Fact]
    public void TheSourceViewScrollsAndCanBeSelected()
    {
        output.WriteLine("source text " + fixture.SourceText);

        fixture.SourceText.CanScrollVertically.Should().BeTrue();
        fixture.SourceText.HasVisibleSelectableSurface.Should().BeTrue(
            "a reader has to be able to select and copy a line of the translation");
    }

    internal static (ContentAreaMeasurement Formatted, ContentAreaMeasurement SourceText, IReadOnlyList<string> EveryBlockKind)
        MeasureBoth()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bt-preview-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, "big.md");
        File.WriteAllText(path, Long());

        try
        {
            var preview = new FilePreviewViewModel();
            preview.OpenAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            preview.TranslatedText = Long();
            preview.HasTranslation = true;

            return StaRunner.Run(() =>
            {
                // Restored on the way out. Application.Current is process-wide,
                // and while these dictionaries are merged into it
                // Tokens.TryMilliseconds starts answering for keys it otherwise
                // reports as absent -- which silently switched SkeletonGate onto
                // its timed path and failed ChatNamingSkeletonTests about one run
                // in three.
                var added = LoadAppResources();

                try
                {
                    return (Measure(preview, formatted: true), Measure(preview, formatted: false), BlockKinds());
                }
                finally
                {
                    foreach (var dictionary in added)
                    {
                        Application.Current!.Resources.MergedDictionaries.Remove(dictionary);
                    }
                }
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ContentAreaMeasurement Measure(FilePreviewViewModel preview, bool formatted)
    {
        if (formatted)
        {
            preview.ShowFormattedViewCommand.Execute(null);
        }
        else
        {
            preview.ShowSourceViewCommand.Execute(null);
        }

        var view = new App.Views.FilePreviewView { DataContext = preview, Width = 520 };

        view.Measure(new Size(520, 700));
        view.Arrange(new Rect(0, 0, 520, 700));
        view.UpdateLayout();

        var scroll = Descendants<ScrollViewer>(view).First();

        return new ContentAreaMeasurement(
            scroll.ViewportWidth,
            scroll.ViewportHeight,
            scroll.ExtentWidth,
            scroll.ExtentHeight,
            scroll.ScrollableHeight,
            Descendants<TextBoxBase>(scroll).Any(IsOnScreen));
    }

    /// <summary>
    /// One document carrying every construct the renderer maps, so a block type
    /// that stops rendering is a failing name in a list rather than a gap nobody
    /// notices.
    /// </summary>
    private static IReadOnlyList<string> BlockKinds()
    {
        const string Markdown = """
            # Heading

            A paragraph.

            - first item
            - second item

            | a | b |
            |---|---|
            | 1 | 2 |

            > quoted

            ---

            ```
            code
            ```
            """;

        var blocks = BetterTranslator.Indexing.Markdown.MarkdownBlockParser.Parse(Markdown);

        return [.. App.Controls.MarkdownFlow.Build(blocks).Blocks.Select(b => b.GetType().Name)];
    }

    private static bool IsOnScreen(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

    private static List<ResourceDictionary> LoadAppResources()
    {
        string[] names =
        [
            "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Icons",
            "Themes/Flags", "Themes/Controls", "Themes/Markdown", "Themes/Json",
            "Verification/VerificationTokens",
        ];

        var added = new List<ResourceDictionary>();

        foreach (var name in names)
        {
            var source = new Uri($"pack://application:,,,/BetterTranslator;component/{name}.xaml", UriKind.Absolute);

            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != source))
            {
                var dictionary = new ResourceDictionary { Source = source };

                Application.Current.Resources.MergedDictionaries.Add(dictionary);
                added.Add(dictionary);
            }
        }

        return added;
    }

    private static string Long()
    {
        var text = new StringBuilder();

        for (var i = 0; i < 400; i++)
        {
            text.Append("## Oddil ").Append(i).Append('\n').Append('\n')
                .Append("Radek ").Append(i)
                .Append(" prekladu, ktery je dost dlouhy na to, aby se zalamoval pres celou sirku panelu.\n\n");
        }

        return text.ToString();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
