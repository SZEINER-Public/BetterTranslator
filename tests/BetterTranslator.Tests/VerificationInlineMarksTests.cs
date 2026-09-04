using System.Collections.Generic;
using System.Linq;
using BetterTranslator.App.Verification;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Gate;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class VerificationInlineMarksTests
{
    private const string Text = "Se soubor ukládá a nastavení zůstává.";

    private static RoutedFinding Routed(string checkId, int offset, int length, CheckSeverity severity, CheckAction action) =>
        new(new CheckFinding(checkId, new CheckRange("/0", offset, length), null, CheckGranularity.Word, severity, 80, CheckCause.ModelOutput, checkId + " evidence", action), RoutingTable.CategoryOf(checkId), GateStage.Escalation, action, 0, [checkId], [CheckCause.ModelOutput]);

    private static GateRunResult Gate(IReadOnlyList<RoutedFinding> routed) =>
        new(100.0, 0, routed, [.. routed.Where(r => r.Severity == CheckSeverity.Defect)], [], [], new Dictionary<string, int>(), [], [], [], string.Empty, string.Empty, string.Empty);

    [Fact]
    public void A_rewrite_finding_gets_the_suggestion_tier_and_a_defect_gets_the_defect_tier()
    {
        var result = VerificationResult.Skipped("no dictionary");
        result.Gate = Gate([Routed("NAT-103", 0, 2, CheckSeverity.Score, CheckAction.Rewrite), Routed("COV-104", 10, 6, CheckSeverity.Defect, CheckAction.Repair)]);

        var marks = VerificationInlineRenderer.Marks(Text, result);

        marks.Should().Contain(m => m.Start == 0 && m.Length == 2 && m.Tier == InlineTier.Suggestion);
        marks.Should().Contain(m => m.Start == 10 && m.Length == 6 && m.Tier == InlineTier.Defect);
    }

    [Fact]
    public void A_defect_outranks_a_suggestion_on_the_same_range_and_the_old_tiers_stay()
    {
        var result = new VerificationResult
        {
            Executed = true,
            Spans = [new VerificationSpan { Start = 3, Length = 6, Word = "soubor", Score = 40, Tier = SeverityTier.Warning }],
        };
        result.Gate = Gate([Routed("NAT-104", 3, 6, CheckSeverity.Score, CheckAction.Rewrite), Routed("STR-106", 3, 6, CheckSeverity.Defect, CheckAction.Repair)]);

        var marks = VerificationInlineRenderer.Marks(Text, result);

        marks.Should().ContainSingle(m => m.Start == 3 && m.Length == 6);
        marks.Single(m => m.Start == 3).Tier.Should().Be(InlineTier.Defect);
        marks.Single(m => m.Start == 3).Tooltip.Should().Contain("Warning 40/100").And.Contain("NAT-104");
    }

    [Fact]
    public void Without_a_gate_result_only_the_existing_tiers_render()
    {
        var result = new VerificationResult
        {
            Executed = true,
            Spans = [new VerificationSpan { Start = 3, Length = 6, Word = "soubor", Score = 20, Tier = SeverityTier.Error }],
        };

        var marks = VerificationInlineRenderer.Marks(Text, result);

        marks.Should().ContainSingle();
        marks[0].Tier.Should().Be(InlineTier.Error);
    }
}
