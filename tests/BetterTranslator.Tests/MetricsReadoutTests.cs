using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The Advanced readout under a chat entry: what the run cost on top, what the
/// audit measured under it. The demotion of the second tier is the whole point
/// of the block, and it is carried by three axes at once, so each is pinned
/// here. A later hand that flattens one of them has to fail a test to do it.
///
/// The last case renders the block offscreen to `artifacts/`, which is the only
/// way to look at this surface without starting the window.
/// </summary>
public sealed class MetricsReadoutTests
{
    [Fact]
    public void The_audit_tier_is_subordinate_on_size_weight_and_colour()
    {
        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var cost = Style("TextEntryCost");
            var audit = Style("TextEntryAudit");

            var costSize = (double)Effective(cost, System.Windows.Controls.TextBlock.FontSizeProperty)!;
            var auditSize = (double)Effective(audit, System.Windows.Controls.TextBlock.FontSizeProperty)!;

            costSize.Should().BeGreaterThan(auditSize, "size is the axis a reader sees first");
            (costSize - auditSize).Should().BeGreaterThanOrEqualTo(1, "half a point is not a step");

            var costWeight = (FontWeight)Effective(cost, System.Windows.Controls.TextBlock.FontWeightProperty)!;
            var auditWeight = (FontWeight)Effective(audit, System.Windows.Controls.TextBlock.FontWeightProperty)!;

            costWeight.Should().NotBe(auditWeight, "the cost row carries the only weight contrast here");

            var costInk = (SolidColorBrush)Effective(cost, System.Windows.Controls.TextBlock.ForegroundProperty)!;
            var auditInk = (SolidColorBrush)Effective(audit, System.Windows.Controls.TextBlock.ForegroundProperty)!;

            Luminance(auditInk).Should().BeGreaterThan(
                Luminance(costInk),
                "the audit tier sits one step lighter on the text ramp");

            return null;
        }));

        failure.Should().BeNull();
    }

    [Fact]
    public void Both_tiers_share_one_pitch()
    {
        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var cost = Effective(Style("TextEntryCost"), System.Windows.Controls.TextBlock.LineHeightProperty);
            var audit = Effective(Style("TextEntryAudit"), System.Windows.Controls.TextBlock.LineHeightProperty);

            audit.Should().Be(cost, "two sizes on one pitch, so the pair reads as one block");

            return null;
        }));

        failure.Should().BeNull();
    }

    [Fact]
    public void The_findings_cell_leaves_the_grey_only_when_something_was_found()
    {
        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var findings = Style("TextEntryAuditFindings");

            Effective(findings, System.Windows.Controls.TextBlock.ForegroundProperty)
                .Should().Be(
                    Effective(Style("TextEntryAudit"), System.Windows.Controls.TextBlock.ForegroundProperty),
                    "a clean entry says so in the same grey as the rest of its tier");

            var trigger = findings.Triggers.OfType<DataTrigger>().Single();

            trigger.Value.Should().Be("True");
            ((System.Windows.Data.Binding)trigger.Binding).Path.Path.Should().Be("HasDefects");
            trigger.Setters.OfType<Setter>().Single().Property
                .Should().Be(System.Windows.Controls.TextBlock.ForegroundProperty);

            return null;
        }));

        failure.Should().BeNull();
    }

    [Fact]
    public void The_readout_renders()
    {
        var rows = new[]
        {
            new Row
            {
                TokenFigure = "4 156 tokens", DurationFigure = "57 s", RateFigure = "72,9 tok/s",
                CoverageFigure = "96,7% translated",
                DefectFigure = "3 spans kept, 3 markup defects", HasDefects = true,
            },
            new Row
            {
                TokenFigure = "312 tokens", DurationFigure = "4,1 s", RateFigure = "76,1 tok/s",
                CoverageFigure = "99,4% translated", DefectFigure = "no defects",
            },
            new Row
            {
                TokenFigure = "9 480 tokens", DurationFigure = "412,7 s", RateFigure = "23 tok/s",
                CoverageFigure = "95,8% translated", LineFigure = "219/179 lines", UnitFigure = "168/129 units",
                DefectFigure = "12 spans kept, 3 citations lost, 7 markup defects", HasDefects = true,
            },
            new Row
            {
                HasMetrics = false, TimeMarker = "11:31",
                CoverageFigure = "98,1% translated", DefectFigure = "no defects",
            },
        };

        var written = new List<string>();

        var failure = StaRunner.Run(() => WithTokens(() =>
        {
            var root = (FrameworkElement)XamlReader.Parse(Markup);

            root.DataContext = rows;
            root.Width = ReadingWidth;
            root.Measure(new Size(ReadingWidth, double.PositiveInfinity));
            root.Arrange(new Rect(new Point(0, 0), root.DesiredSize));
            root.UpdateLayout();

            root.ActualHeight.Should().BeGreaterThan(0);

            foreach (var scale in new[] { 1d, 2d })
            {
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(root.ActualWidth * scale),
                    (int)Math.Ceiling(root.ActualHeight * scale),
                    96 * scale,
                    96 * scale,
                    PixelFormats.Pbgra32);

                bitmap.Render(root);

                var path = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "artifacts", $"metrics-readout-{scale:0}x.png"));

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var encoder = new PngBitmapEncoder();

                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var stream = File.Create(path);

                encoder.Save(stream);
                written.Add(path);
            }

            return null;
        }));

        failure.Should().BeNull();
        written.Should().OnlyContain(path => new FileInfo(path).Length > 0);
    }

    private const double ReadingWidth = 740;

    private sealed class Row
    {
        public string TimeMarker { get; init; } = "11:28";
        public bool HasMetrics { get; init; } = true;
        public string TokenFigure { get; init; } = string.Empty;
        public string DurationFigure { get; init; } = string.Empty;
        public string RateFigure { get; init; } = string.Empty;
        public bool HasFidelity { get; init; } = true;
        public string CoverageFigure { get; init; } = string.Empty;
        public string LineFigure { get; init; } = string.Empty;
        public string UnitFigure { get; init; } = string.Empty;
        public string DefectFigure { get; init; } = string.Empty;
        public bool HasDefects { get; init; }
    }

    /// <summary>
    /// The same two runs ChatView draws, without the format toggle and the
    /// bilingual row above them. Kept in one string so the probe cannot drift
    /// into testing a layout the window does not have.
    /// </summary>
    private const string Markup = """
<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Background="{StaticResource BrushSurface}"
        Padding="24,18,24,18">
    <ItemsControl ItemsSource="{Binding}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <Grid Margin="0,14,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>

                    <TextBlock Grid.Column="0"
                               Text="{Binding TimeMarker}"
                               Style="{StaticResource TextMeta}"
                               Foreground="{StaticResource BrushTextMuted}"
                               VerticalAlignment="Center" />

                    <StackPanel Grid.Column="1" HorizontalAlignment="Right">
                        <WrapPanel HorizontalAlignment="Right"
                                   Visibility="{Binding HasMetrics, Converter={StaticResource BoolToVisibility}}">
                            <TextBlock Text="{Binding TokenFigure}" Style="{StaticResource TextEntryCost}" Margin="0" />
                            <TextBlock Text="{Binding DurationFigure}" Style="{StaticResource TextEntryCost}" />
                            <TextBlock Text="{Binding RateFigure}" Style="{StaticResource TextEntryCost}"
                                       Visibility="{Binding RateFigure, Converter={StaticResource NotEmptyToVisibility}}" />
                        </WrapPanel>

                        <WrapPanel HorizontalAlignment="Right"
                                   Visibility="{Binding HasFidelity, Converter={StaticResource BoolToVisibility}}">
                            <TextBlock Text="{Binding CoverageFigure}" Style="{StaticResource TextEntryAudit}" Margin="0" />
                            <TextBlock Text="{Binding LineFigure}" Style="{StaticResource TextEntryAudit}"
                                       Visibility="{Binding LineFigure, Converter={StaticResource NotEmptyToVisibility}}" />
                            <TextBlock Text="{Binding UnitFigure}" Style="{StaticResource TextEntryAudit}"
                                       Visibility="{Binding UnitFigure, Converter={StaticResource NotEmptyToVisibility}}" />
                            <TextBlock Text="{Binding DefectFigure}" Style="{StaticResource TextEntryAuditFindings}" />
                        </WrapPanel>
                    </StackPanel>
                </Grid>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</Border>
""";

    private static Style Style(string key) => (Style)Application.Current!.Resources[key];

    /// <summary>
    /// A setter reached through however many BasedOn steps declare it, nearest
    /// first. Reading Setters alone answers for the wrong style: the size these
    /// tiers differ by is declared three levels up.
    /// </summary>
    private static object? Effective(Style? style, DependencyProperty property)
    {
        for (var step = style; step is not null; step = step.BasedOn)
        {
            var setter = step.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == property);

            if (setter is not null)
            {
                return setter.Value;
            }
        }

        return null;
    }

    private static double Luminance(SolidColorBrush brush) =>
        (0.2126 * brush.Color.R) + (0.7152 * brush.Color.G) + (0.0722 * brush.Color.B);

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
            var source = new Uri(
                $"pack://application:,,,/BetterTranslator;component/{name}.xaml",
                UriKind.Absolute);

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
        "Themes/Colors", "Themes/Typography", "Themes/Metrics", "Themes/Controls",
    ];
}
