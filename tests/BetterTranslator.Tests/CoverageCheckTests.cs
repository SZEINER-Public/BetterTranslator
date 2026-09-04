using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class CoverageCheckTests
{
    private static readonly CheckRunSettings EnglishToCzech = new() { SourceLanguage = "en", TargetLanguage = "cs" };

    private static readonly string[] SourceLines =
    [
        "Open the settings window and pin the sidebar to your workspace.",
        "When the document is saved, the list refreshes.",
        "Upgrading from 2.3 needs no migration.",
        "Your settings carry over.",
    ];

    private static readonly string[] TargetLines =
    [
        "Otevřete okno nastavení a připněte postranní lištu ke svému pracovnímu prostoru.",
        "Když je dokument uložen, seznam se obnoví.",
        "Aktualizace z verze 2.3 nevyžaduje žádnou migraci.",
        "Vaše nastavení se přenese.",
    ];

    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) =>
        CheckRegistry.Default.Find(checkId)!.Run(context);

    private static IReadOnlyList<CheckFinding> RunAll(CheckContext context) =>
        [.. CheckRegistry.Default.ForCategory(CheckId.Coverage.Category).SelectMany(check => check.Run(context))];

    private static (string Source, string Target, IReadOnlyList<SegmentTrace> Traces) Lines(
        IReadOnlyList<string> source,
        IReadOnlyList<string?> target,
        IReadOnlyList<SegmentOutcome>? outcomes = null)
    {
        var sourceText = string.Join('\n', source);
        var targetText = string.Join('\n', target.Where(t => t is not null));
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            var outcome = outcomes?[i] ?? (target[i] is null ? SegmentOutcome.Dropped : SegmentOutcome.Translated);
            var answer = target[i];

            traces.Add(answer is null
                ? new SegmentTrace(sourceAt, source[i].Length, outcome)
                : new SegmentTrace(sourceAt, source[i].Length, outcome, answer, null, answer, targetAt, answer.Length));

            sourceAt += source[i].Length + 1;

            if (answer is not null)
            {
                targetAt += answer.Length + 1;
            }
        }

        return (sourceText, targetText, traces);
    }

    private static CheckContext Context(
        IReadOnlyList<string> source,
        IReadOnlyList<string?> target,
        IEnumerable<ExemptSpan>? exemptions = null,
        IReadOnlyList<SegmentOutcome>? outcomes = null)
    {
        var (sourceText, targetText, traces) = Lines(source, target, outcomes);
        return StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, exemptions, EnglishToCzech);
    }

    [Fact]
    public void Six_coverage_checks_register_in_order()
    {
        var ids = CheckRegistry.Default.ForCategory(CheckId.Coverage.Category).Select(check => check.CheckId).ToList();

        ids.Should().Equal(
            CheckId.Coverage.CopyThrough,
            CheckId.Coverage.EmptyOutput,
            CheckId.Coverage.DroppedUnit,
            CheckId.Coverage.SourceTokenSurvival,
            CheckId.Coverage.SegmentLanguage,
            CheckId.Coverage.ExemptionFilter);
    }

    [Fact]
    public void Copy_through_passes_on_a_translated_run_and_fires_on_an_unchanged_unit()
    {
        Run(CheckId.Coverage.CopyThrough, Context(SourceLines, TargetLines)).Should().BeEmpty();

        var copied = TargetLines.ToArray();
        copied[2] = "Upgrading from 2.3 needs no migration!";

        var findings = Run(CheckId.Coverage.CopyThrough, Context(SourceLines, copied));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Severity == CheckSeverity.Defect
            && f.Confidence == 100
            && f.Granularity == CheckGranularity.Block
            && f.CauseCode == CheckCause.ModelOutput
            && f.SourceRange != null
            && f.SourceRange.Offset == SourceLines[0].Length + SourceLines[1].Length + 2);
    }

    [Fact]
    public void Empty_output_passes_on_a_filled_run_and_fires_on_a_blank_unit()
    {
        Run(CheckId.Coverage.EmptyOutput, Context(SourceLines, TargetLines)).Should().BeEmpty();

        var blank = TargetLines.ToArray();
        blank[1] = "   ";

        var findings = Run(CheckId.Coverage.EmptyOutput, Context(SourceLines, blank));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Severity == CheckSeverity.Defect
            && f.Confidence == 100
            && f.TargetRange.Offset == TargetLines[0].Length + 1
            && f.Evidence.Contains("came back empty", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Dropped_unit_passes_on_a_complete_run_and_fires_on_a_missing_unit()
    {
        Run(CheckId.Coverage.DroppedUnit, Context(SourceLines, TargetLines)).Should().BeEmpty();

        string?[] dropped = [TargetLines[0], null, TargetLines[2], TargetLines[3]];
        var findings = Run(CheckId.Coverage.DroppedUnit, Context(SourceLines, dropped));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Severity == CheckSeverity.Defect
            && f.Confidence == 100
            && f.Granularity == CheckGranularity.Block
            && f.CauseCode == CheckCause.Segmentation
            && f.SourceRange != null
            && f.SourceRange.Offset == SourceLines[0].Length + 1
            && f.TargetRange.Offset == TargetLines[0].Length);
    }

    [Fact]
    public void Source_token_survival_needs_two_signals_for_a_word_level_defect()
    {
        Run(CheckId.Coverage.SourceTokenSurvival, Context(SourceLines, TargetLines)).Should().BeEmpty();

        string[] source = ["Open the settings window and save the changes."];
        string[] survived = ["Otevřete okno settings a uložte změny."];

        var findings = Run(CheckId.Coverage.SourceTokenSurvival, Context(source, survived));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Granularity == CheckGranularity.Word
            && f.Severity == CheckSeverity.Defect
            && f.TargetRange.Offset == survived[0].IndexOf("settings", System.StringComparison.Ordinal)
            && f.TargetRange.Length == "settings".Length
            && f.SourceRange != null
            && f.SourceRange.Offset == source[0].IndexOf("settings", System.StringComparison.Ordinal)
            && f.Evidence.Contains("lexicon+morphology", System.StringComparison.Ordinal));

        var evidence = CoverageServices.Default.Evaluate("settings", "en", "cs");
        evidence.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Source_token_survival_with_one_signal_raises_the_granularity_instead()
    {
        string[] source = ["Open the settings window and run."];
        string[] survived = ["Otevřete okno nastavení a run."];

        CoverageServices.Default.Evaluate("run", "en", "cs").Count.Should().Be(1);

        var findings = Run(CheckId.Coverage.SourceTokenSurvival, Context(source, survived));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Granularity == CheckGranularity.Sentence
            && f.Severity == CheckSeverity.Score
            && f.Confidence <= 35
            && f.TargetRange.Offset == 0
            && f.TargetRange.Length == survived[0].Length
            && f.Evidence.Contains("1 signal", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Segment_language_passes_on_czech_and_fires_on_an_english_unit_above_the_floor()
    {
        Run(CheckId.Coverage.SegmentLanguage, Context(SourceLines, TargetLines)).Should().BeEmpty();

        var english = TargetLines.ToArray();
        english[0] = "Please open the settings window and pin the sidebar to your workspace now.";

        var findings = Run(CheckId.Coverage.SegmentLanguage, Context(SourceLines, english));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Severity == CheckSeverity.Defect
            && f.Granularity >= CheckGranularity.Sentence
            && f.TargetRange.Offset == 0
            && f.Evidence.Contains("reads as 'en'", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Segment_language_is_undetermined_below_the_character_floor_and_raises_nothing()
    {
        var identifier = CoverageServices.Default.Identifier;

        identifier.Identify("Save the changes now").Should().Be(LanguageIdentification.Undetermined);
        identifier.Identify("Please save all of the changes now.").Code.Should().Be("en");
        identifier.Identify("Uložte prosím všechny změny nyní.").Code.Should().Be("cs");
        CharNgramLanguageIdentifier.MeasuredLength("Save the changes now").Should().Be(20);

        string[] source = ["Save the changes now", "Open the settings window"];
        string[] target = ["Save the changes now", "Otevřete okno nastavení"];

        Run(CheckId.Coverage.SegmentLanguage, Context(source, target)).Should().BeEmpty();
    }

    [Fact]
    public void Exemption_filter_passes_on_honored_spans_and_reports_a_stale_span()
    {
        string[] source = ["Open Bubble Desktop and save the changes."];
        string[] target = ["Otevřete Bubble Desktop a uložte změny."];
        var at = target[0].IndexOf("Bubble Desktop", System.StringComparison.Ordinal);
        var honored = new ExemptSpan(new CheckRange("/0", at, "Bubble Desktop".Length), ExemptionReason.ProtectedName, "Bubble Desktop");

        Run(CheckId.Coverage.ExemptionFilter, Context(source, target, [honored])).Should().BeEmpty();

        var stale = new ExemptSpan(new CheckRange("/0", at, "Bubble Desktop".Length), ExemptionReason.ProtectedName, "Bubble Client");
        var findings = Run(CheckId.Coverage.ExemptionFilter, Context(source, target, [stale]));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Severity == CheckSeverity.Advisory
            && f.Action == CheckAction.ScoreOnly
            && f.TargetRange.Offset == at
            && f.Evidence.Contains("was not subtracted", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_protected_span_that_would_look_untranslated_produces_no_coverage_finding()
    {
        string[] source = ["Bubble Desktop", "Open the settings window and save the changes."];
        string[] target = ["Bubble Desktop", "Otevřete okno nastavení a uložte změny."];
        var protectedName = new ExemptSpan(new CheckRange("/0", 0, "Bubble Desktop".Length), ExemptionReason.ProtectedName, "Bubble Desktop");

        var unfiltered = Context(source, target);
        Run(CheckId.Coverage.CopyThrough, unfiltered).Should().ContainSingle();

        var filtered = Context(source, target, [protectedName]);
        RunAll(filtered).Should().BeEmpty();

        var report = CompletionMetric.Compute(filtered);
        report.ExemptUnits.Should().Be(1);
        report.Total.Should().Be(1);
        report.Translated.Should().Be(1);
        report.PercentText.Should().Be("100.0");
    }

    [Fact]
    public void A_reordered_clause_raises_the_granularity_ceiling_instead_of_a_wrong_word_pair()
    {
        string[] source = ["When the file is saved, the index updates and the queue drains."];
        string[] target = ["Index se aktualizuje a fronta se vyprázdní, když je soubor uložen."];

        var context = Context(source, target);
        var alignment = CoverageAlignment.Of(context);
        var pair = alignment.Pairs.Should().ContainSingle().Subject;

        pair.Ceiling.Should().NotBe(CheckGranularity.Word);
        pair.Confidence.Should().BeLessThan(CoverageAlignment.WordCeilingFloor);
        pair.Anchors.Where(a => a.Identical).Should().OnlyContain(a => a.Source.Key == a.Target.Key);

        RunAll(context).Should().NotContain(f => f.Granularity == CheckGranularity.Word);

        string[] straight = ["Open the settings window and save the changes."];
        string[] straightTarget = ["Otevřete okno nastavení a uložte změny."];

        CoverageAlignment.Of(Context(straight, straightTarget)).Pairs.Single().Ceiling.Should().Be(CheckGranularity.Word);
    }

    [Fact]
    public void A_whole_chunk_returned_untranslated_lowers_the_completion_metric()
    {
        var copied = TargetLines.ToArray();
        copied[0] = SourceLines[0];

        var context = Context(SourceLines, copied);
        var findings = RunAll(context);

        findings.Should().Contain(f => f.CheckId == CheckId.Coverage.CopyThrough && f.Severity == CheckSeverity.Defect && f.TargetRange.Offset == 0);
        findings.Should().Contain(f => f.CheckId == CheckId.Coverage.SegmentLanguage && f.TargetRange.Offset == 0);

        var report = CompletionMetric.Compute(context);

        report.Total.Should().Be(4);
        report.Translated.Should().Be(3);
        report.ExemptUnits.Should().Be(0);
        report.PercentText.Should().Be("75.0");
        report.AlignmentConfidence.Should().BeInRange(1, 100);
        report.ToString().Should().Be($"75.0% (3/4 units, 0 exempt, alignment {report.AlignmentConfidence})");
    }

    [Fact]
    public void Completion_metric_excludes_exempt_units_from_both_sides()
    {
        string[] source = ["https://bubble.example.com/docs", "Open the settings window.", "Save the changes.", "Close the window."];
        string?[] target = ["https://bubble.example.com/docs", "Otevřete okno nastavení.", null, "Zavřete okno."];
        var url = new ExemptSpan(new CheckRange("/0", 0, source[0].Length), ExemptionReason.Url, source[0]);

        var report = CompletionMetric.Compute(Context(source, target, [url]));

        report.Total.Should().Be(3);
        report.Translated.Should().Be(2);
        report.ExemptUnits.Should().Be(1);
        report.PercentText.Should().Be("66.7");
    }

    [Fact]
    public void Findings_are_deterministic_across_runs()
    {
        var copied = TargetLines.ToArray();
        copied[0] = SourceLines[0];
        copied[3] = "  ";

        var first = RunAll(Context(SourceLines, copied));
        var second = RunAll(Context(SourceLines, copied));

        first.Should().NotBeEmpty();
        second.Should().Equal(first);
    }

    [Fact]
    public void Chunk_pairing_without_traces_aligns_lines_by_position()
    {
        var (source, target, _) = Lines(SourceLines, TargetLines);
        var context = StructureContext.Build(source, target, ProseStructure.Instance, settings: EnglishToCzech);

        var alignment = CoverageAlignment.Of(context);

        alignment.FromTrace.Should().BeFalse();
        alignment.Pairs.Should().HaveCount(4);
        alignment.Pairs.Should().OnlyContain(p => p.HasTarget);
        RunAll(context).Should().BeEmpty();
    }
}


