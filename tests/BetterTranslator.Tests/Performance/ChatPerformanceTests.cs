using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests.Performance;

public sealed class PerfFactAttribute : FactAttribute
{
    public const string Gate = "BT_PERF";

    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Gate) != "1")
        {
            Skip = $"Set {Gate}=1 to run. Seeds a corpus and measures the chat switch path.";
        }
    }
}

public sealed class ChatPerformanceTests(ITestOutputHelper output)
{
    private static string ReportPath =>
        Environment.GetEnvironmentVariable("BT_PERF_OUT")
        ?? Path.Combine(Path.GetTempPath(), "bt-perf-report.txt");

    [PerfFact]
    public async Task MeasureTheSwitchScenarios()
    {
        using var harness = new ChatSwitchHarness();
        await harness.SeedAsync();

        var lines = new List<string>();

        var cold = await harness.SingleSwitchAsync("single switch, cold", harness.Corpus[0]);
        lines.Add(cold.ToString());

        var warm = await harness.SingleSwitchAsync("single switch, warm", harness.Corpus[0]);
        lines.Add(warm.ToString());

        var burst = await harness.BurstAsync("burst of three", harness.Burst);
        lines.Add(burst.ToString());

        var report = string.Join(Environment.NewLine, lines);

        File.WriteAllText(ReportPath, report + Environment.NewLine, new UTF8Encoding(false));
        output.WriteLine(report);
        output.WriteLine($"written to {ReportPath}");

        cold.ViewModelsMaterialized.Should().BeGreaterThan(0, "the corpus is not empty");
    }

    /// <summary>
    /// The burst as it stands today, recorded rather than asserted against a
    /// budget. Three selections issued faster than one load completes build
    /// three conversations, because the load takes no cancellation token: the
    /// figure is the defect, and the assertion that it must be about one belongs
    /// with the fix rather than ahead of it.
    /// </summary>
    [PerfFact]
    public async Task ABurstOfThreeSwitchesBuildsAboutOneConversation()
    {
        using var harness = new ChatSwitchHarness();
        await harness.SeedAsync();

        var single = await harness.SingleSwitchAsync("single", harness.Corpus[0]);
        var burst = await harness.BurstAsync("burst", harness.Burst);

        output.WriteLine($"single materialized {single.ViewModelsMaterialized}");
        output.WriteLine($"burst  materialized {burst.ViewModelsMaterialized}");
        output.WriteLine(ChatSwitchHarness.Diagnostic);

        burst.ViewModelsMaterialized.Should().BeLessThan(
            single.ViewModelsMaterialized * 2,
            "a cancelled load must build no further view model");
    }
}



