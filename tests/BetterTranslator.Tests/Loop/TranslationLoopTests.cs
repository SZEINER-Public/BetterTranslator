using System.IO;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests.Loop;

public sealed class LoopFactAttribute : FactAttribute
{
    public const string Gate = "BT_LOOP_CORPUS";

    public LoopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Gate) != "1")
        {
            Skip = $"Set {Gate}=1 to run. Loads a local model and translates a corpus slice.";
        }
    }
}

public sealed class TranslationLoopTests(ITestOutputHelper output)
{
    private static string SourcePath =>
        Environment.GetEnvironmentVariable("BT_LOOP_SOURCE")
        ?? Path.Combine(Profile, "Downloads", "MEMORY.md");

    private static string CandidatePath =>
        Environment.GetEnvironmentVariable("BT_LOOP_CANDIDATE")
        ?? Path.Combine(Profile, "Downloads", "message.txt");

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static int Number(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    [Fact]
    public void ScorePair()
    {
        File.Exists(SourcePath).Should().BeTrue($"the reference source must be at {SourcePath}");
        File.Exists(CandidatePath).Should().BeTrue($"the defective output must be at {CandidatePath}");

        var source = DocumentUnderTest.FromFile(SourcePath);
        var candidate = DocumentUnderTest.FromFile(CandidatePath);

        var metrics = TranslationChecker.Score(source, candidate);

        Report(metrics, "measured baseline pair");

        metrics.SourceHeadings.Should().Be(42);
        metrics.HeadingsUntranslated.Should().Be(29);
        metrics.FrontMatterProseUntranslated.Should().Be(1);
        metrics.ByteOrderMarkLost.Should().Be(1);
        metrics.ResidualSourceSentences.Should().Be(40);
        metrics.NumericTokensLost.Should().Be(22);
        metrics.SourceWordBeforeBacktick.Should().Be(604);
        metrics.SourceWordAfterBacktick.Should().Be(625);
        metrics.CandidateWordBeforeBacktick.Should().Be(828);
        metrics.CandidateWordAfterBacktick.Should().Be(892);
    }

    [LoopFact]
    public async Task RunCorpus()
    {
        File.Exists(SourcePath).Should().BeTrue($"the reference source must be at {SourcePath}");

        var model = LoopHarness.ResolveModel();
        model.Should().NotBeNull("the corpus loop needs the translategemma GGUF on disk");

        LoopHarness.PreferInstalledBackend();
        ApplyIncumbent();

        var source = DocumentUnderTest.FromFile(SourcePath);
        var split = LoopCorpus.Split(source, Number("BT_LOOP_TUNING", 4), Number("BT_LOOP_HELDOUT", 2));

        output.WriteLine($"sections total: {split.TotalSections}");
        output.WriteLine($"tuning slice:   {split.Tuning.Sections} sections, {split.Tuning.Document.Text.Length} chars");
        output.WriteLine($"held-out slice: {split.HeldOut.Sections} sections, {split.HeldOut.Document.Text.Length} chars");

        using var harness = new LoopHarness(model!, SourcePath);

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(Number("BT_LOOP_MINUTES", 30)));

        var loaded = await harness.WarmAsync(lifetime.Token);
        loaded.Should().BeTrue($"the model must load: {harness.LoadFailure}");

        foreach (var slice in Slices(split))
        {
            var run = await harness.RunAsync(slice, lifetime.Token);
            var metrics = TranslationChecker.Score(slice.Document, run.Candidate, runtimeFailures: run.RuntimeFailures);

            output.WriteLine(string.Empty);
            output.WriteLine($"=== {slice.Name} ===");
            output.WriteLine($"wall clock:     {run.Elapsed.TotalSeconds:F1} s");
            output.WriteLine($"chars/second:   {run.CharactersPerSecond(slice.Document.Text.Length):F1}");
            output.WriteLine($"requests:       {run.Requests}");
            output.WriteLine($"tokens/unit:    {run.TokensPerUnit:F1}");
            output.WriteLine($"blocks:         {run.BlocksTranslated} translated, {run.BlocksRecovered} recovered, {run.BlocksKept} kept");
            output.WriteLine($"runtime fails:  {run.RuntimeFailures}");

            Report(metrics, slice.Name);
        }
    }

    private static IEnumerable<CorpusSlice> Slices(CorpusSplit split)
    {
        if (Environment.GetEnvironmentVariable("BT_LOOP_TUNING_RUN") != "0")
        {
            yield return split.Tuning;
        }

        if (Environment.GetEnvironmentVariable("BT_LOOP_HELDOUT_RUN") == "1")
        {
            yield return split.HeldOut;
        }
    }

    private static void ApplyIncumbent()
    {
        if (Environment.GetEnvironmentVariable("BT_LOOP_INCUMBENT") != "1")
        {
            return;
        }

        BetterTranslator.Engine.Config.PipelineOptions.PreserveByteOrderMark = false;
        BetterTranslator.Engine.Config.PipelineOptions.TranslateFrontMatterProse = false;
        BetterTranslator.Engine.Config.PipelineOptions.RestoreAsciiPunctuation = false;
        BetterTranslator.Engine.Config.PipelineOptions.TrimIntroducedTrailingBlanks = false;
        BetterTranslator.Engine.Config.PipelineOptions.DropInventedMarkup = false;
        BetterTranslator.Engine.Config.PipelineOptions.EscalateRetryDecoding = false;
    }

    private void Report(TranslationMetrics metrics, string label)
    {
        output.WriteLine($"--- {label} ---");

        foreach (var (gate, count) in metrics.Gates)
        {
            output.WriteLine($"{gate}: {count}");
        }

        foreach (var (defect, count) in metrics.Defects)
        {
            output.WriteLine($"{defect}: {count}");
        }

        output.WriteLine($"G1 detail: code {metrics.CodeSpansLost}, links {metrics.LinkTargetsLost}, paths {metrics.PathsLost}, versions {metrics.VersionsLost}, numbers {metrics.NumericTokensLost}, names {metrics.ProductNamesLost}, invented {metrics.ProtectedTokensInvented}");
        output.WriteLine($"G2 detail: missing {metrics.UnitsMissing}, out of band {metrics.UnitsOutOfBand}, units {metrics.SourceUnits}");
        output.WriteLine($"G3 detail: residue {metrics.ResidualSourceSentences}, not cs {metrics.UnitsNotInTargetLanguage}");
        output.WriteLine($"G4 detail: headings {metrics.HeadingCountDelta}/{metrics.HeadingLevelMismatches}/{metrics.HeadingsUntranslated}, frontmatter {metrics.FrontMatterProseUntranslated}, lists {metrics.ListMarkerDelta}, tables {metrics.TableRowDelta}, paragraphs {metrics.ParagraphDelta}, blanks {metrics.BlankLineTopologyMismatches}, bold {metrics.BoldMarkerDelta}, italic {metrics.ItalicMarkerDelta}");
        output.WriteLine($"G5 detail: bom {metrics.ByteOrderMarkLost}, newline {metrics.LineEndingChanged}, trailing {metrics.TrailingWhitespaceIntroduced}, typographic {metrics.TypographicSubstitutions}, encoding {metrics.EncodingFaults}");
        output.WriteLine($"G6 detail: backtick {metrics.SourceWordBeforeBacktick}/{metrics.SourceWordAfterBacktick} -> {metrics.CandidateWordBeforeBacktick}/{metrics.CandidateWordAfterBacktick}, excess {metrics.BoundaryFusionExcess}");
        output.WriteLine($"scores: headings {metrics.TranslatedHeadingRate:F1}, terminology {metrics.TerminologyConsistency:F1}, morphology {metrics.MorphologicalLegality:F1}, fluency {metrics.FluencyProxy:F1}, mean {metrics.ScoreTotal:F1}");
    }
}
