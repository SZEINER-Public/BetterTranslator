using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Runtime;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Structure;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Verification;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RecordedRuntimeResponse
{
    public required string Path { get; init; }

    public required ProbabilityAvailability Availability { get; init; }

    public required IReadOnlyList<(string Segment, string Source, string Answer)> Segments { get; init; }

    public required IReadOnlyList<SegmentProbabilities> Probabilities { get; init; }

    public required IReadOnlyDictionary<string, AdequacyScore> ForcedDecode { get; init; }

    public static RecordedRuntimeResponse Load(string name)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Runtime", name);
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;

        var segments = new List<(string, string, string)>();
        var probabilities = new List<SegmentProbabilities>();

        foreach (var element in root.GetProperty("segments").EnumerateArray())
        {
            var identity = element.GetProperty("segment").GetString()!;
            var answer = element.GetProperty("answer").GetString()!;
            var tokens = new List<TokenProbability>();
            var offset = 0;

            foreach (var token in element.GetProperty("tokens").EnumerateArray())
            {
                var text = token.GetProperty("text").GetString()!;
                tokens.Add(new TokenProbability(tokens.Count, text, offset, text.Length, token.GetProperty("logProb").GetDouble()));
                offset += text.Length;
            }

            segments.Add((identity, element.GetProperty("source").GetString()!, answer));
            probabilities.Add(new SegmentProbabilities(identity, answer, tokens));
        }

        var forced = root.GetProperty("forcedDecode").EnumerateArray().ToDictionary(
            e => e.GetProperty("segment").GetString()!,
            e => new AdequacyScore(e.GetProperty("segment").GetString()!, e.GetProperty("lengthNormalizedLogProb").GetDouble(), e.GetProperty("scoredTokens").GetInt32()),
            StringComparer.Ordinal);

        var availability = root.GetProperty("availability");

        return new RecordedRuntimeResponse
        {
            Path = root.GetProperty("path").GetString()!,
            Availability = new ProbabilityAvailability(availability.GetProperty("available").GetBoolean(), availability.GetProperty("reason").GetString()!),
            Segments = segments,
            Probabilities = probabilities,
            ForcedDecode = forced,
        };
    }

    public CapturedProbabilityPort Port(bool withForcedDecode = true) =>
        new(Probabilities, withForcedDecode ? (identity, _, _) => ForcedDecode.GetValueOrDefault(identity) : null);
}

public sealed class RuntimeCheckTests
{
    private static readonly CheckRunSettings EnglishToCzech = new() { SourceLanguage = "en", TargetLanguage = "cs" };

    private static readonly RecordedRuntimeResponse Recorded = RecordedRuntimeResponse.Load("recorded-completion.json");

    private static CheckContext Context(IReadOnlyList<(string Segment, string Source, string Answer)> segments, IReadOnlyList<string>? targets = null, IRuntimeProbabilityPort? port = null)
    {
        var sourceText = string.Join('\n', segments.Select(s => s.Source));
        var targetLines = targets ?? [.. segments.Select(s => s.Answer)];
        var targetText = string.Join('\n', targetLines);
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var (_, source, answer) = segments[i];
            traces.Add(new SegmentTrace(sourceAt, source.Length, SegmentOutcome.Translated, answer, null, targetLines[i], targetAt, targetLines[i].Length));
            sourceAt += source.Length + 1;
            targetAt += targetLines[i].Length + 1;
        }

        var context = StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, null, EnglishToCzech);

        if (port is not null)
        {
            RuntimePorts.Attach(context, port);
        }

        return context;
    }

    private static CheckContext Exact(IRuntimeProbabilityPort? port = null) => Context(Recorded.Segments, null, port ?? Recorded.Port());

    private static CheckContext Lossy(IRuntimeProbabilityPort? port = null)
    {
        var targets = Recorded.Segments.Select(s => s.Answer).ToList();
        targets[2] = targets[2].TrimEnd('.');
        return Context(Recorded.Segments, targets, port ?? Recorded.Port());
    }

    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) =>
        CheckRegistry.Default.Find(checkId)!.Run(context);

    private static int TargetOffset(int line) =>
        Recorded.Segments.Take(line).Sum(s => s.Answer.Length + 1);

    [Fact]
    public void Three_runtime_checks_register_in_order()
    {
        CheckRegistry.Default.ForCategory(CheckId.Runtime.Category).Select(c => c.CheckId).Should().Equal(
            CheckId.Runtime.SequenceConfidence,
            CheckId.Runtime.SpanLocalization,
            CheckId.Runtime.ForcedDecodeAdequacy);

        Recorded.Segments.Select(s => s.Segment).Should().Equal(Exact().Alignment.Select(a => a.Identity));
    }

    [Fact]
    public void Per_token_logprobs_are_unavailable_on_the_current_host_path_and_every_check_reports_skipped()
    {
        Recorded.Availability.Available.Should().BeFalse();
        Recorded.Availability.Reason.Should().Be(UnavailableProbabilityPort.HostProtocolReason);

        var capture = new HostProbabilityCapture();
        capture.Availability.Available.Should().BeFalse();
        capture.Capture("line#0", new Completion("Otevřete okno nastavení a uložte změny.", 9, null)).Should().BeNull();
        capture.TryGetLogprobs("line#0", out var logprobs, out var reason).Should().BeFalse();
        logprobs.Should().BeEmpty();
        reason.Should().Be(UnavailableProbabilityPort.HostProtocolReason);

        var context = Context(Recorded.Segments, null, capture.Port());
        var report = RuntimeRunReport.Build(context);

        report.Availability.Available.Should().BeFalse();
        report.Checks.Should().HaveCount(3);
        report.Checks.Should().OnlyContain(c => c.State == RuntimeCheckState.Skipped && c.Reason == UnavailableProbabilityPort.HostProtocolReason && !c.CountsTowardPassTotal);
        report.Ran.Should().Be(0);
        report.Skipped.Should().Be(3);
        report.Findings.Should().BeEmpty();
        report.MeanSequenceConfidence.Should().BeNull();
        report.Summary().Should().StartWith("per-token logprobs unavailable: ");

        foreach (var check in CheckRegistry.Default.ForCategory(CheckId.Runtime.Category))
        {
            check.Run(context).Should().BeEmpty();
        }

        RuntimePorts.For(Context(Recorded.Segments)).Availability.Available.Should().BeFalse();
    }

    [Fact]
    public void Sequence_confidence_reads_mean_and_min_logprob_per_captured_segment()
    {
        var findings = Run(CheckId.Runtime.SequenceConfidence, Exact());

        findings.Should().HaveCount(3);
        findings.Should().OnlyContain(f => f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly && f.Granularity == CheckGranularity.Sentence);
        findings.Select(f => f.TargetRange.Offset).Should().Equal(TargetOffset(0), TargetOffset(1), TargetOffset(2));

        var confident = Recorded.Probabilities[0];
        var doubtful = Recorded.Probabilities[1];
        confident.SequenceConfidence.Should().BeGreaterThan(doubtful.SequenceConfidence);
        findings[0].Confidence.Should().BeLessThan(findings[1].Confidence);
        findings[1].Evidence.Should().Contain("min logprob -2.5");
        findings[1].Confidence.Should().Be((int)Math.Round(100 - doubtful.SequenceConfidence, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Span_localization_maps_a_low_probability_run_to_the_expected_target_span()
    {
        var context = Exact();
        var findings = Run(CheckId.Runtime.SpanLocalization, context);
        var answer = Recorded.Segments[1].Answer;
        var expectedOffset = TargetOffset(1) + answer.IndexOf(" pilníku", StringComparison.Ordinal);

        var localized = findings.Should().Contain(f => f.TargetRange.Offset == expectedOffset).Subject;
        localized.Granularity.Should().Be(CheckGranularity.Word);
        localized.TargetRange.Length.Should().Be(" pilníku".Length);
        context.Target.Text.Substring(localized.TargetRange.Offset, localized.TargetRange.Length).Should().Be(" pilníku");
        localized.Evidence.Should().Contain("tokens 3 to 4");
        localized.Confidence.Should().Be(new LowProbabilityRun(3, 4, 0, 0, -2.2).Confidence);

        findings.Should().OnlyContain(f => f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly);
        RuntimeCheck.LowRuns(Recorded.Probabilities[1]).Should().ContainSingle().Which.Should().Match<LowProbabilityRun>(r => r.FirstToken == 3 && r.LastToken == 4);
    }

    [Fact]
    public void Span_localization_raises_granularity_to_sentence_where_the_offset_map_is_lossy()
    {
        var context = Lossy();
        var findings = Run(CheckId.Runtime.SpanLocalization, context);
        var start = TargetOffset(2);

        var raised = findings.Should().ContainSingle(f => f.TargetRange.Offset == start).Subject;
        raised.Granularity.Should().Be(CheckGranularity.Sentence);
        raised.TargetRange.Length.Should().Be(Recorded.Segments[2].Answer.Length - 1);
        raised.Evidence.Should().Contain("lossy");

        var exact = Run(CheckId.Runtime.SpanLocalization, Exact()).Single(f => f.TargetRange.Offset >= start);
        exact.Granularity.Should().Be(CheckGranularity.Word);
        exact.TargetRange.Length.Should().Be(" nyní".Length);
    }

    [Fact]
    public void Forced_decode_runs_at_most_once_per_flagged_segment_and_is_cached_across_checks()
    {
        var port = Recorded.Port();
        var context = Exact(port);
        var flagged = Recorded.Probabilities.Count(p => RuntimeCheck.LowRuns(p).Count > 0);

        var first = Run(CheckId.Runtime.ForcedDecodeAdequacy, context);
        port.ForcedDecodeCalls.Should().Be(flagged);

        var second = Run(CheckId.Runtime.ForcedDecodeAdequacy, context);
        port.ForcedDecodeCalls.Should().Be(flagged);
        second.Should().Equal(first);

        first.Should().OnlyContain(f => f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly && f.Evidence.Contains("forced-decode adequacy"));
        first.Single(f => f.TargetRange.Offset == TargetOffset(1)).Confidence.Should().Be((int)Math.Round(100 - Recorded.ForcedDecode["line#1"].Adequacy, MidpointRounding.AwayFromZero));

        var withoutScorer = Recorded.Port(withForcedDecode: false);
        var report = RuntimeRunReport.Build(Exact(withoutScorer));
        report.Checks.Single(c => c.CheckId == CheckId.Runtime.ForcedDecodeAdequacy).Should().Match<RuntimeCheckStatus>(c => c.State == RuntimeCheckState.Skipped && c.Reason.Contains("forced-decode"));
        report.Ran.Should().Be(2);
        withoutScorer.ForcedDecodeCalls.Should().Be(0);
    }

    [Fact]
    public void The_run_report_carries_availability_and_mean_sequence_confidence_for_the_fixture_corpus()
    {
        var report = RuntimeRunReport.Build(Exact());

        report.Availability.Available.Should().BeTrue();
        report.Ran.Should().Be(3);
        report.Skipped.Should().Be(0);
        report.CapturedSegments.Should().Be(3);
        report.MeanSequenceConfidence.Should().Be(Math.Round(Recorded.Probabilities.Average(p => p.SequenceConfidence), 1, MidpointRounding.AwayFromZero));
        report.Summary().Should().StartWith("per-token logprobs available; 3 ran, 0 skipped");
    }

    [Fact]
    public void An_exempt_span_produces_no_runtime_finding_and_findings_are_deterministic()
    {
        var segment = Recorded.Segments[1];
        var start = TargetOffset(1);
        var whole = new ExemptSpan(new CheckRange("/1", start, segment.Answer.Length), ExemptionReason.SettingsRule, segment.Answer);
        var sourceText = string.Join('\n', Recorded.Segments.Select(s => s.Source));
        var targetText = string.Join('\n', Recorded.Segments.Select(s => s.Answer));
        var context = StructureContext.Build(sourceText, targetText, ProseStructure.Instance, Exact().Alignment.Select(a => new SegmentTrace(a.SourceRange.Offset, a.SourceRange.Length, a.Outcome, null, null, null, a.TargetRange!.Offset, a.TargetRange.Length)).ToList(), [whole], EnglishToCzech);
        RuntimePorts.Attach(context, Recorded.Port());

        var all = CheckRegistry.Default.ForCategory(CheckId.Runtime.Category).SelectMany(c => c.Run(context)).ToList();
        all.Should().NotBeEmpty();
        all.Should().NotContain(f => f.TargetRange.Offset >= start && f.TargetRange.End <= start + segment.Answer.Length);

        var again = CheckRegistry.Default.ForCategory(CheckId.Runtime.Category).SelectMany(c => c.Run(context)).ToList();
        again.Should().Equal(all);
    }

    [Fact]
    public void Runtime_findings_feed_the_existing_word_score_without_changing_the_scale_or_tiers()
    {
        var settings = new VerificationSettings();
        var verifier = new TranslationVerifier(new AcceptAllSpeller(), null, null, null, settings);
        var context = Exact();
        var findings = CheckRegistry.Default.ForCategory(CheckId.Runtime.Category).SelectMany(c => c.Run(context)).ToList();
        var target = context.Target.Text;

        var scored = verifier.Verify(context.Source.Text, target, findings);
        var plain = verifier.Verify(context.Source.Text, target);

        plain.Spans.Should().OnlyContain(s => s.Score == 100);
        scored.Spans.Should().OnlyContain(s => s.Score >= 0 && s.Score <= 100);
        scored.Spans.Single(s => s.Word == "pilníku").Signals.Should().Contain(h => h.SignalId == CheckId.Runtime.SpanLocalization);
        scored.Spans.Single(s => s.Word == "pilníku").Score.Should().BeLessThan(scored.Spans.Single(s => s.Word == "Otevřete").Score);
        scored.Spans.Should().OnlyContain(s => s.Tier == (s.Score < settings.ErrorThreshold ? SeverityTier.Error : s.Score < settings.WarningThreshold ? SeverityTier.Warning : SeverityTier.Clean));
    }

    private sealed class AcceptAllSpeller : BetterTranslator.Engine.Verification.Signals.ISpellChecker
    {
        public bool IsCorrect(string word) => true;
    }
}
