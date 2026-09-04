using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Ratio;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public static class RatioFixtures
{
    public static readonly CheckRunSettings EnglishToCzech = new() { SourceLanguage = "en", TargetLanguage = "cs" };

    public static RatioProfile AllEnabled { get; } = RatioProfile.Default with
    {
        Checks = [.. RatioProfile.Default.Checks.Select(c => c with { Enabled = true })],
    };

    public static CheckContext Context(IReadOnlyList<string> source, IReadOnlyList<string> target, IEnumerable<ExemptSpan>? exemptions = null)
    {
        var sourceText = string.Join('\n', source);
        var targetText = string.Join('\n', target);
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            traces.Add(new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Translated, target[i], null, target[i], targetAt, target[i].Length));
            sourceAt += source[i].Length + 1;
            targetAt += target[i].Length + 1;
        }

        return StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, exemptions, EnglishToCzech);
    }
}

public sealed class RatioCheckTests
{
    private const string Source = "Open the settings window and pin the sidebar to your workspace.";

    private const string Target = "Otevřete okno nastavení a připněte postranní lištu ke svému pracovnímu prostoru.";

    private static RatioBandSet Sentence => RatioProfile.Default.BandFor("en-cs", RatioUnitType.Sentence)!;

    private static IReadOnlyList<CheckFinding> Run<TCheck>(CheckContext context) where TCheck : RatioCheck
    {
        var check = (RatioCheck)System.Activator.CreateInstance(typeof(TCheck), RatioFixtures.AllEnabled)!;
        return check.Run(context);
    }

    private static IReadOnlyList<CheckFinding> RunAll(CheckContext context) =>
    [
        .. new RatioCheck[]
        {
            new LengthRatioCheck(RatioFixtures.AllEnabled),
            new TruncationCheck(RatioFixtures.AllEnabled),
            new RepetitionCheck(RatioFixtures.AllEnabled),
            new CompressionCheck(RatioFixtures.AllEnabled),
            new InsertionCheck(RatioFixtures.AllEnabled),
        }.SelectMany(check => check.Run(context)),
    ];

    [Fact]
    public void Five_ratio_checks_register_in_order()
    {
        CheckRegistry.Default.ForCategory(CheckId.Ratio.Category).Select(check => check.CheckId).Should().Equal(
            CheckId.Ratio.LengthRatio,
            CheckId.Ratio.Truncation,
            CheckId.Ratio.Repetition,
            CheckId.Ratio.Compression,
            CheckId.Ratio.Insertion);

        CheckRegistry.Default.ForCategory(CheckId.Ratio.Category).Should().OnlyContain(check => ((RatioCheck)check).Profile == RatioProfile.Default);
    }

    [Fact]
    public void Every_ratio_finding_is_score_only_and_names_its_threshold()
    {
        var loop = Target + string.Concat(Enumerable.Repeat(" ke svému pracovnímu prostoru", 8));
        var findings = RunAll(RatioFixtures.Context([Source, "Here is the translation."], [loop, "Here is the translation: Zde je překlad."]));

        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(f => f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly);
        findings.Should().OnlyContain(f => f.Evidence.Contains("threshold", System.StringComparison.Ordinal) && f.Confidence >= 1 && f.Confidence <= 100);
        findings.Should().OnlyContain(f => f.SourceRange != null);
    }

    [Fact]
    public void Length_ratio_passes_on_a_correct_pair_and_fires_on_a_doubled_target()
    {
        Run<LengthRatioCheck>(RatioFixtures.Context([Source], [Target])).Should().BeEmpty();

        var findings = Run<LengthRatioCheck>(RatioFixtures.Context([Source], [Target + " " + Target]));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Granularity == CheckGranularity.Sentence
            && f.TargetRange.Offset == 0
            && f.TargetRange.Length == Target.Length * 2 + 1
            && f.Evidence.Contains("length ratio", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_correct_translation_at_the_band_edge_raises_nothing()
    {
        var band = Sentence.LengthRatio;
        var sourceLength = RatioMeasures.Collapse(Source).Length;
        var atEdge = new string('a', (int)System.Math.Floor(sourceLength * band.High));

        RatioMeasures.LengthRatio(Source, atEdge).Should().BeLessThanOrEqualTo(band.High);
        Run<LengthRatioCheck>(RatioFixtures.Context([Source], [atEdge])).Should().BeEmpty();

        var justInside = new string('a', (int)System.Math.Ceiling(sourceLength * band.Low));
        RatioMeasures.LengthRatio(Source, justInside).Should().BeGreaterThanOrEqualTo(band.Low);
        Run<LengthRatioCheck>(RatioFixtures.Context([Source], [justInside])).Should().BeEmpty();

        RunAll(RatioFixtures.Context([Source], [Target])).Should().NotContain(f => f.Severity == CheckSeverity.Defect);
    }

    [Fact]
    public void Truncation_passes_on_a_finished_target_and_fires_on_a_cut_one()
    {
        Run<TruncationCheck>(RatioFixtures.Context([Source], [Target])).Should().BeEmpty();

        var cut = "Otevřete okno nastavení a";
        var findings = Run<TruncationCheck>(RatioFixtures.Context([Source], [cut]));

        findings.Should().ContainSingle().Which.Should().Match<CheckFinding>(f =>
            f.Severity == CheckSeverity.Score
            && f.Action == CheckAction.ScoreOnly
            && f.Evidence.Contains("terminal punctuation", System.StringComparison.Ordinal));

        Run<TruncationCheck>(RatioFixtures.Context(["Open the settings window and pin the sidebar"], [cut])).Should().BeEmpty();
    }

    [Fact]
    public void Repetition_passes_on_prose_and_fires_on_a_loop()
    {
        Run<RepetitionCheck>(RatioFixtures.Context([Source], [Target])).Should().BeEmpty();

        var loop = "Otevřete okno nastavení a připněte a připněte a připněte a připněte a připněte a připněte a připněte postranní lištu.";
        var findings = Run<RepetitionCheck>(RatioFixtures.Context([Source], [loop]));

        findings.Should().ContainSingle().Which.Evidence.Should().Contain("repeated n-gram run");
        RatioMeasures.RepetitionRun(loop).Should().BeGreaterThan((int)Sentence.RepetitionRun.Limit);
    }

    [Fact]
    public void Compression_passes_on_prose_and_fires_on_a_degenerate_loop()
    {
        Run<CompressionCheck>(RatioFixtures.Context([Source], [Target])).Should().BeEmpty();

        var loop = Target + string.Concat(Enumerable.Repeat(" ke svému pracovnímu prostoru", 12));
        var findings = Run<CompressionCheck>(RatioFixtures.Context([Source], [loop]));

        findings.Should().ContainSingle().Which.Evidence.Should().Contain("compression ratio");
        RatioMeasures.CompressionRatio(loop).Should().BeGreaterThan(Sentence.CompressionRatio.Limit);
    }

    [Fact]
    public void Insertion_passes_on_prose_and_fires_on_a_preamble_leak_and_on_extra_sentences()
    {
        Run<InsertionCheck>(RatioFixtures.Context([Source], [Target])).Should().BeEmpty();

        var preamble = Run<InsertionCheck>(RatioFixtures.Context([Source], ["Here is the translation: " + Target]));
        preamble.Should().ContainSingle().Which.Evidence.Should().Contain("model text");

        var czechPreamble = Run<InsertionCheck>(RatioFixtures.Context([Source], ["Zde je překlad: " + Target]));
        czechPreamble.Should().ContainSingle();

        var extra = Target + string.Concat(Enumerable.Repeat(" Toto je další věta.", (int)Sentence.SentenceExcess.Limit + 3));
        var invented = Run<InsertionCheck>(RatioFixtures.Context([Source], [extra]));
        invented.Should().ContainSingle().Which.Evidence.Should().Contain("sentence count excess");
    }

    [Fact]
    public void An_exempt_span_produces_no_ratio_finding()
    {
        var loop = Target + string.Concat(Enumerable.Repeat(" ke svému pracovnímu prostoru", 12));
        var whole = new ExemptSpan(new CheckRange("/0", 0, loop.Length), ExemptionReason.SettingsRule, loop);

        RunAll(RatioFixtures.Context([Source], [loop], [whole])).Should().BeEmpty();
    }

    [Fact]
    public void Findings_are_deterministic()
    {
        var loop = Target + string.Concat(Enumerable.Repeat(" ke svému pracovnímu prostoru", 12));
        var first = RunAll(RatioFixtures.Context([Source], [loop]));
        var second = RunAll(RatioFixtures.Context([Source], [loop]));

        first.Should().NotBeEmpty();
        second.Should().Equal(first);
    }

    [Fact]
    public void Ratio_findings_lower_the_existing_word_score_without_changing_the_scale_or_tiers()
    {
        var settings = new VerificationSettings();
        var verifier = new TranslationVerifier(new AcceptAllSpeller(), null, null, null, settings);
        var loop = Target + string.Concat(Enumerable.Repeat(" ke svému pracovnímu prostoru", 12));
        var context = RatioFixtures.Context([Source], [loop]);
        var findings = RunAll(context);

        var plain = verifier.Verify(Source, loop);
        var scored = verifier.Verify(Source, loop, findings);

        plain.Spans.Should().OnlyContain(s => s.Score == 100 && s.Tier == SeverityTier.Clean);
        scored.Spans.Should().OnlyContain(s => s.Score >= 0 && s.Score <= 100);
        scored.Spans.Should().Contain(s => s.Score < 100 && s.Signals.Any(h => h.SignalId.StartsWith("RAT-", System.StringComparison.Ordinal)));
        scored.Spans.Select(s => s.Tier).Should().OnlyContain(t => t == SeverityTier.Clean || t == SeverityTier.Warning || t == SeverityTier.Error);
        scored.Spans.Should().OnlyContain(s => s.Tier == (s.Score < settings.ErrorThreshold ? SeverityTier.Error : s.Score < settings.WarningThreshold ? SeverityTier.Warning : SeverityTier.Clean));
    }

    private sealed class AcceptAllSpeller : BetterTranslator.Engine.Verification.Signals.ISpellChecker
    {
        public bool IsCorrect(string word) => true;
    }
}
