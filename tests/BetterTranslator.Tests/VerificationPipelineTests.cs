using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Coverage;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class VerificationPipelineTests
{
    private static readonly string[] Source = ["Open the settings window.", "Save the file."];

    private static readonly string[] Dropped = ["Otevřete okno nastavení."];

    private static IReadOnlyList<SegmentTrace> Traces(IReadOnlyList<string> source, IReadOnlyList<string?> target)
    {
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            var answer = i < target.Count ? target[i] : null;
            traces.Add(answer is null
                ? new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Dropped)
                : new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Translated, answer, null, answer, targetAt, answer.Length));
            sourceAt += source[i].Length + 1;
            targetAt += answer is null ? 0 : answer.Length + 1;
        }

        return traces;
    }

    [Fact]
    public void Without_a_dictionary_the_gate_still_runs_and_rides_on_a_skipped_result()
    {
        var pipeline = new VerificationPipeline(null, new VerificationSettings());

        var result = pipeline.Verify(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en", "cs");

        result.Executed.Should().BeFalse();
        result.SkipReason.Should().Be(VerificationPipeline.NoDictionaryReason);
        result.Gate.Should().NotBeNull();
        result.Gate!.Defects.Should().Contain(d => d.CheckId == CheckId.Coverage.DroppedUnit);
        result.Gate.RepairCandidates.Should().NotBeEmpty();
        result.Gate.CompletionPercent.Should().Be(50.0);
    }

    [Fact]
    public void Toggles_off_produce_an_empty_gate_and_no_findings()
    {
        var settings = new VerificationSettings();
        settings.Gate.Enabled = false;
        var pipeline = new VerificationPipeline(null, settings);

        var result = pipeline.Verify(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en", "cs");

        result.Gate!.Routed.Should().BeEmpty();
        result.Gate.Findings.Should().BeEmpty();
        result.Gate.Checks.Should().OnlyContain(c => c.State == GateCheckState.Skipped);
    }

    [Fact]
    public void Escalation_admits_flagged_spans_before_the_semantic_stage()
    {
        CheckContext? seen = null;
        var pipeline = new VerificationPipeline(null, new VerificationSettings())
        {
            Semantics = context =>
            {
                seen = context;
                return SemanticServices.Unavailable("fixture");
            },
        };

        string[] copied = ["Otevřete okno nastavení.", "Save the file."];
        pipeline.Verify(string.Join('\n', Source), string.Join('\n', copied), Traces(Source, copied), "en", "cs");

        seen.Should().NotBeNull();
        EscalationGate.DecisionFor(seen!).Should().NotBeNull();
        EscalationGate.DecisionFor(seen!)!.Flagged.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Coverage_evidence_factory_configures_the_seeded_lexicon_without_a_dictionary()
    {
        var evidence = CoverageEvidenceFactory.Create("en", null);

        evidence.Evaluate("settings", "en", "cs").Lexicon.Should().BeTrue();
        evidence.Evaluate("nastavení", "en", "cs").Lexicon.Should().BeFalse();

        CoverageServices.Configure(evidence);
        CoverageServices.Default.Should().BeSameAs(evidence);
        CoverageServices.Configure(null);
    }

    [Fact]
    public void Language_codes_are_reduced_to_their_primary_subtag()
    {
        var pipeline = new VerificationPipeline(null, new VerificationSettings());

        var gate = pipeline.RunGate(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en-US", "cs-CZ");

        gate.Defects.Select(d => d.CheckId).Should().Contain(CheckId.Coverage.DroppedUnit);
    }
}
