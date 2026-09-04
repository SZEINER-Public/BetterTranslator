using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class VerificationGateTests
{
    private const string Source = "Open the settings window.\nSave the file.";

    private const string Target = "Otevřete okno nastavení.\nUložte soubor.";

    private sealed class FixtureCheck(string checkId, Func<CheckContext, IReadOnlyList<CheckFinding>> run) : ICheck
    {
        public string CheckId => checkId;

        public string Category => RoutingTable.CategoryOf(checkId);

        public IReadOnlyList<CheckFinding> Run(CheckContext context) => run(context);
    }

    private sealed class ThrowingCheck : ICheck
    {
        public string CheckId => "STR-999";

        public string Category => "STR";

        public IReadOnlyList<CheckFinding> Run(CheckContext context) => throw new InvalidOperationException("fixture failure");
    }

    private static CheckFinding Finding(
        string checkId,
        int offset,
        int length,
        CheckSeverity severity = CheckSeverity.Defect,
        int confidence = 80,
        CheckGranularity granularity = CheckGranularity.Word,
        CheckAction action = CheckAction.Repair,
        string cause = CheckCause.ModelOutput,
        string unit = "/0") =>
        new(checkId, new CheckRange(unit, offset, length), null, granularity, severity, confidence, cause, checkId + " evidence", action);

    private static CheckRegistry Registry(params ICheck[] checks)
    {
        var registry = new CheckRegistry();

        foreach (var check in checks)
        {
            registry.Register(check);
        }

        return registry;
    }

    private static ICheck Emitting(string checkId, params CheckFinding[] findings) => new FixtureCheck(checkId, _ => findings);

    private static CheckContext Context(IEnumerable<ExemptSpan>? exemptions = null)
    {
        var traces = new List<SegmentTrace>
        {
            new(0, 25, SegmentOutcome.Translated, "Otevřete okno nastavení.", null, "Otevřete okno nastavení.", 0, 24),
            new(26, 14, SegmentOutcome.Translated, "Uložte soubor.", null, "Uložte soubor.", 25, 14),
        };

        return StructureContext.Build(Source, Target, ProseStructure.Instance, traces, exemptions, new CheckRunSettings { SourceLanguage = "en", TargetLanguage = "cs" });
    }

    [Fact]
    public void An_exempt_span_produces_no_routed_finding_whatever_its_severity()
    {
        var exemption = new ExemptSpan(new CheckRange("/0", 9, 4), ExemptionReason.ProtectedName, "okno");
        var registry = Registry(
            Emitting("STR-101", Finding("STR-101", 9, 4, CheckSeverity.Defect, 99)),
            Emitting("RAT-101", Finding("RAT-101", 10, 2, CheckSeverity.Score, 90)),
            Emitting("COV-104", Finding("COV-104", 0, 8, CheckSeverity.Defect, 70)));

        var result = new VerificationGate(registry).Run(Context([exemption]));

        result.ExemptCount.Should().Be(2);
        result.Routed.Should().ContainSingle(f => f.CheckId == "COV-104");
        result.Defects.Should().ContainSingle();
        result.RepairCandidates.Should().ContainSingle(f => f.CheckId == "COV-104");
    }

    [Fact]
    public void Ratio_semantic_and_runtime_findings_never_carry_repair()
    {
        var registry = Registry(
            Emitting("RAT-101", Finding("RAT-101", 0, 8, CheckSeverity.Defect, 95, action: CheckAction.Repair)),
            Emitting("SEM-101", Finding("SEM-101", 25, 6, CheckSeverity.Defect, 95, action: CheckAction.Repair)),
            Emitting("RUN-101", Finding("RUN-101", 9, 4, CheckSeverity.Defect, 95, action: CheckAction.Repair)));

        var result = new VerificationGate(registry).Run(Context());

        result.Routed.Should().HaveCount(3);
        result.Routed.Should().OnlyContain(f => f.Action == CheckAction.ScoreOnly);
        result.RepairCandidates.Should().BeEmpty();
        result.Findings.Should().OnlyContain(f => f.Action == CheckAction.ScoreOnly);
        result.ScoreContributions.Should().HaveCount(3);
    }

    [Fact]
    public void Overlapping_findings_merge_into_one_carrier_with_every_cause_code()
    {
        var registry = Registry(
            Emitting("STR-101", Finding("STR-101", 9, 4, CheckSeverity.Defect, 60, cause: CheckCause.Masking)),
            Emitting("COV-104", Finding("COV-104", 11, 6, CheckSeverity.Defect, 90, cause: CheckCause.ModelOutput)));

        var result = new VerificationGate(registry).Run(Context());

        result.Routed.Should().ContainSingle();
        var carrier = result.Routed[0];
        carrier.CheckId.Should().Be("COV-104");
        carrier.Confidence.Should().Be(90);
        carrier.CauseCodes.Should().Equal(CheckCause.Masking, CheckCause.ModelOutput);
        carrier.CheckIds.Should().Equal("COV-104", "STR-101");
        carrier.Finding.CauseCode.Should().Be("masking+model-output");
    }

    [Fact]
    public void A_later_stage_never_overturns_an_earlier_defect()
    {
        var registry = Registry(
            Emitting("STR-101", Finding("STR-101", 9, 4, CheckSeverity.Defect, 60)),
            Emitting("RAT-101", Finding("RAT-101", 9, 4, CheckSeverity.Score, 99, action: CheckAction.ScoreOnly)),
            Emitting("RUN-101", Finding("RUN-101", 9, 4, CheckSeverity.Score, 50, action: CheckAction.ScoreOnly)));

        var result = new VerificationGate(registry).Run(Context());

        result.Routed.Should().ContainSingle();
        var carrier = result.Routed[0];
        carrier.Severity.Should().Be(CheckSeverity.Defect);
        carrier.Action.Should().Be(CheckAction.Repair);
        carrier.Stage.Should().Be(GateStage.Deterministic);
        carrier.Priority.Should().Be(1);
        result.Defects.Should().ContainSingle();
    }

    [Fact]
    public void A_word_candidate_inside_a_flagged_block_is_dropped_in_favor_of_the_block()
    {
        var registry = Registry(
            Emitting("STR-101", Finding("STR-101", 0, 24, CheckSeverity.Defect, 60, CheckGranularity.Block)),
            Emitting("TRM-101", Finding("TRM-101", 30, 6, CheckSeverity.Defect, 85, CheckGranularity.Word)),
            Emitting("COV-104", Finding("COV-104", 2, 3, CheckSeverity.Defect, 99, CheckGranularity.Word)));

        var settings = new GateSettings { FindingsPerUnitCap = 0 };
        var result = new VerificationGate(registry, settings).Run(Context());

        result.Routed.Should().HaveCount(3);
        result.RepairCandidates.Select(c => c.CheckId).Should().Equal("STR-101", "TRM-101");
    }

    [Fact]
    public void Candidates_are_ordered_block_then_sentence_then_word_and_by_confidence_inside()
    {
        var registry = Registry(
            Emitting("TRM-101", Finding("TRM-101", 25, 6, CheckSeverity.Defect, 95, CheckGranularity.Word, unit: "/1")),
            Emitting("COV-103", Finding("COV-103", 0, 24, CheckSeverity.Defect, 50, CheckGranularity.Sentence)),
            Emitting("STR-101", Finding("STR-101", 0, 39, CheckSeverity.Defect, 40, CheckGranularity.Block, unit: "/")),
            Emitting("STR-102", Finding("STR-102", 25, 14, CheckSeverity.Defect, 70, CheckGranularity.Sentence)));

        var result = new VerificationGate(registry, new GateSettings { FindingsPerUnitCap = 0 }).Run(Context());

        result.RepairCandidates.Select(c => c.CheckId).Should().Equal("STR-101", "STR-102", "COV-103", "TRM-101");
    }

    [Fact]
    public void The_caps_hold()
    {
        var findings = Enumerable.Range(0, 10).Select(i => Finding("COV-104", i * 2, 1, CheckSeverity.Defect, 50 + i)).ToArray();
        var registry = Registry(Emitting("COV-104", findings));

        var result = new VerificationGate(registry, new GateSettings { RepairCandidateCap = 3, FindingsPerUnitCap = 5 }).Run(Context());

        result.Routed.Should().HaveCount(5);
        result.Routed.Select(f => f.Confidence).Should().BeEquivalentTo([59, 58, 57, 56, 55]);
        result.RepairCandidates.Should().HaveCount(3);
        result.RepairCandidates.Select(f => f.Confidence).Should().Equal(59, 58, 57);
    }

    [Fact]
    public void The_gate_produces_a_correct_run_result_with_only_one_category_present()
    {
        var registry = Registry(Emitting("STR-101", Finding("STR-101", 9, 4)));

        var result = new VerificationGate(registry).Run(Context());

        result.Categories.Should().Equal("STR");
        result.SkippedCategories.Should().Equal("COV", "TRM", "RUN", "RAT", "SEM");
        result.Checks.Where(c => c.State == GateCheckState.Skipped).Should().OnlyContain(c => c.Reason.Contains("no checks discovered"));
        result.Checks.Should().OnlyContain(c => c.State == GateCheckState.Skipped || c.CheckId == "STR-101");
        result.Ran.Should().Be(1);
        result.Skipped.Should().Be(5);
        result.CompletionPercent.Should().Be(100.0);
        result.CompletionText.Should().Be("100.0");
        result.ExemptCount.Should().Be(0);
        result.Defects.Should().ContainSingle();
        result.RedTier.Should().ContainSingle(f => f.CheckId == "STR-101");
        result.FindingCounts.Should().Equal(new Dictionary<string, int> { ["STR-101"] = 1 });
    }

    [Fact]
    public void A_disabled_category_is_reported_as_skipped_not_passed()
    {
        var registry = Registry(Emitting("STR-101", Finding("STR-101", 9, 4)), Emitting("COV-104", Finding("COV-104", 0, 4)));
        var settings = new GateSettings().Disable("COV");

        var result = new VerificationGate(registry, settings).Run(Context());

        result.Routed.Should().ContainSingle(f => f.CheckId == "STR-101");
        result.Checks.Should().Contain(c => c.CheckId == "COV-104" && c.State == GateCheckState.Skipped && c.Reason.Contains("disabled"));
        result.SkippedCategories.Should().Contain("COV");
        result.Checks.Should().NotContain(c => c.CheckId == "COV-104" && c.State == GateCheckState.Ran);
    }

    [Fact]
    public void A_failing_check_is_skipped_with_its_reason_and_the_run_continues()
    {
        var registry = Registry(new ThrowingCheck(), Emitting("STR-101", Finding("STR-101", 9, 4)));

        var result = new VerificationGate(registry).Run(Context());

        result.Checks.Should().Contain(c => c.CheckId == "STR-999" && c.State == GateCheckState.Skipped && c.Reason.Contains("InvalidOperationException"));
        result.Routed.Should().ContainSingle();
    }

    [Fact]
    public void Routing_is_a_table_that_parses_and_extends_without_code()
    {
        var table = RoutingTable.Parse("STR:Defect=Repair;NEW:*=Mark;RUN:*=ScoreOnly+Priority");

        table.ActionFor(Finding("NEW-101", 0, 1, CheckSeverity.Advisory)).Should().Be(CheckAction.Mark);
        table.ActionFor(Finding("STR-101", 0, 1, CheckSeverity.Score)).Should().Be(RoutingTable.FallbackAction);
        table.ActionFor(Finding("XYZ-101", 0, 1)).Should().Be(CheckAction.ScoreOnly);
        table.RaisesPriority(Finding("RUN-101", 0, 1)).Should().BeTrue();
        RoutingTable.Parse(RoutingTable.Default.ToString()).ToString().Should().Be(RoutingTable.Default.ToString());

        RoutingTable.Default.ActionFor(Finding("TRM-101", 0, 1)).Should().Be(CheckAction.Repair);
        RoutingTable.Default.ActionFor(Finding("TRM-102", 0, 1)).Should().Be(CheckAction.Repair);
        RoutingTable.Default.ActionFor(Finding("TRM-103", 0, 1)).Should().Be(CheckAction.Mark);
        RoutingTable.Default.ActionFor(Finding("STR-101", 0, 1, CheckSeverity.Advisory)).Should().Be(CheckAction.Mark);

        StageTable.Parse("Deterministic=STR;Escalation=NEW").StageOf("NEW").Should().Be(GateStage.Escalation);
        StageTable.Default.StageOf("UNKNOWN").Should().Be(GateStage.Escalation);
    }

    [Fact]
    public void The_run_report_names_every_skipped_check_with_its_reason()
    {
        var registry = Registry(Emitting("STR-101", Finding("STR-101", 9, 4)));
        var result = new VerificationGate(registry, new GateSettings().Disable("STR")).Run(Context());
        var folder = Path.Combine(Path.GetTempPath(), "bt-gate-" + Guid.NewGuid().ToString("N"));

        try
        {
            var path = GateRunReport.Write(result, folder, "gate-run.log");
            var text = File.ReadAllText(path);

            text.Should().Contain("completion 100.0%");
            text.Should().Contain("STR-101 [Deterministic] category disabled by setting");
            text.Should().Contain("COV [Deterministic] no checks discovered for this category");
            text.Should().Contain("RAT [Escalation] no checks discovered for this category");
            text.Should().Contain("checks ran 0, skipped 6");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Defaults_preserve_current_behavior()
    {
        var settings = new GateSettings();

        settings.Enabled.Should().BeTrue();
        settings.DisabledCategories.Should().BeEmpty();
        settings.RoutingText.Should().Be(RoutingTable.DefaultText);
        settings.StageText.Should().Be(StageTable.DefaultText);
        new BetterTranslator.Core.Verification.VerificationSettings().Gate.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Two_runs_produce_identical_results()
    {
        var registry = Registry(
            Emitting("STR-101", Finding("STR-101", 9, 4, CheckSeverity.Defect, 60, cause: CheckCause.Masking)),
            Emitting("COV-104", Finding("COV-104", 11, 6, CheckSeverity.Defect, 90)),
            Emitting("RAT-101", Finding("RAT-101", 25, 6, CheckSeverity.Score, 40, action: CheckAction.ScoreOnly)));

        var first = new VerificationGate(registry).Run(Context());
        var second = new VerificationGate(registry).Run(Context());

        GateRunReport.Render(first).Should().Be(GateRunReport.Render(second));
    }
}
