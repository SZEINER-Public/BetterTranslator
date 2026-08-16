using System.IO;
using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests.Performance;

/// <summary>
/// Defect 2: dragging a selection across a long message. The budget is p95 under
/// 16.7 ms with no frame over 50 ms.
/// </summary>
public sealed class SelectionDragTests(ITestOutputHelper output)
{
    private static string ReportPath =>
        Environment.GetEnvironmentVariable("BT_PERF_FRAMES")
        ?? Path.Combine(Path.GetTempPath(), "bt-frame-report.txt");

    [PerfFact]
    public void ASelectionDragAcrossTheLongestMessageHoldsItsFrames()
    {
        var report = FrameTimingHarness.DragAcross(
            FrameTimingHarness.LongDocument(sections: 400),
            stepCharacters: 120,
            steps: 200,
            width: 740,
            height: 900);

        var line = $"selection drag  {report}";

        File.WriteAllText(ReportPath, line + Environment.NewLine, new UTF8Encoding(false));
        output.WriteLine(line);
        output.WriteLine($"written to {ReportPath}");

        report.Frames.Should().NotBeEmpty("a drag with no frames measured nothing");
        report.SelectedCharacters.Should().BeGreaterThan(0, "the selection has to actually grow");
    }
}
