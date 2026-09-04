using System.Globalization;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Gate;

namespace BetterTranslator.Runtime.Agents;

public sealed record RedSpan(string UnitPath, int Offset, int Length, IReadOnlyList<string> CheckIds, string Evidence);

public sealed record SkippedCheck(string CheckId, string Reason);

public sealed record VerificationSummary(
    bool Available,
    string? Reason,
    double? Completion,
    int Exempt,
    int Defects,
    IReadOnlyList<RedSpan> RedSpans,
    int Suggestions,
    int RepairCandidates,
    IReadOnlyList<SkippedCheck> Skipped)
{
    public const string NotRetainedReason = "verification is not retained for stored entries";

    public const string NoPipelineReason = "no verification pipeline ran for this translation";

    public const string GateOffReason = "the verification gate is switched off";

    public string? Escalation { get; init; }

    public string? CompletionText => Completion is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) : null;

    public static VerificationSummary Unavailable(string reason) => new(false, reason, null, 0, 0, [], 0, 0, []);

    public static VerificationSummary From(VerificationResult? result)
    {
        if (result?.Gate is not { } gate)
        {
            return Unavailable(NoPipelineReason);
        }

        if (gate.Checks.Count > 0 && gate.Checks.All(c => c.State == GateCheckState.Skipped) && gate.Categories.Count == 0)
        {
            return Unavailable(gate.Checks[0].Reason.Length > 0 ? gate.Checks[0].Reason : GateOffReason);
        }

        return new VerificationSummary(
            true,
            null,
            gate.CompletionPercent,
            gate.ExemptCount,
            gate.Defects.Count,
            [.. gate.Defects.Select(d => new RedSpan(d.TargetRange.UnitPath, d.TargetRange.Offset, d.TargetRange.Length, d.CheckIds, d.Finding.Evidence))],
            gate.Routed.Count(r => r.Action == CheckAction.Rewrite),
            gate.RepairCandidates.Count,
            [.. gate.Checks.Where(c => c.State == GateCheckState.Skipped).Select(c => new SkippedCheck(c.CheckId, c.Reason))])
        {
            Escalation = gate.Escalation?.Text,
        };
    }

    public object Payload() =>
        new
        {
            available = Available,
            reason = Reason,
            completion = Completion,
            exempt = Exempt,
            defects = Defects,
            red_spans = RedSpans.Select(s => new { unit = s.UnitPath, offset = s.Offset, length = s.Length, checks = s.CheckIds, evidence = s.Evidence }).ToArray(),
            suggestions = Suggestions,
            repair_candidates = RepairCandidates,
            skipped = Skipped.Select(s => new { check = s.CheckId, reason = s.Reason }).ToArray(),
            escalation = Escalation,
        };

    public Dictionary<string, object?> Envelope() =>
        new()
        {
            ["available"] = Available,
            ["reason"] = Reason,
            ["completion"] = Completion,
            ["exempt"] = Exempt,
            ["defects"] = Defects,
            ["red_spans"] = RedSpans.Select(s => new Dictionary<string, object?>
            {
                ["unit"] = s.UnitPath,
                ["offset"] = s.Offset,
                ["length"] = s.Length,
                ["checks"] = s.CheckIds,
                ["evidence"] = s.Evidence,
            }).ToList(),
            ["suggestions"] = Suggestions,
            ["repair_candidates"] = RepairCandidates,
            ["skipped"] = Skipped.Select(s => new Dictionary<string, object?> { ["check"] = s.CheckId, ["reason"] = s.Reason }).ToList(),
            ["escalation"] = Escalation,
        };

    public const string SchemaObject =
        """
        {
          "type": "object",
          "description": "What the verification layer found on the returned translation. Present on every response; available is false with a reason when no check could run.",
          "required": ["available"],
          "properties": {
            "available": { "type": "boolean" },
            "reason": { "type": ["string", "null"], "description": "Why no verification ran, when available is false." },
            "completion": { "type": ["number", "null"], "description": "Share of translatable units that came back translated, one decimal." },
            "exempt": { "type": "integer", "description": "Findings dropped because they sat on a span intentionally left in the source language." },
            "defects": { "type": "integer", "description": "Spans the checks proved wrong: the red tier." },
            "red_spans": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["unit", "offset", "length", "checks", "evidence"],
                "properties": {
                  "unit": { "type": "string" },
                  "offset": { "type": "integer" },
                  "length": { "type": "integer" },
                  "checks": { "type": "array", "items": { "type": "string" } },
                  "evidence": { "type": "string" }
                }
              }
            },
            "suggestions": { "type": "integer", "description": "Sentences the naturalness checks would restructure." },
            "repair_candidates": { "type": "integer", "description": "Spans the repair pass may re-translate." },
            "skipped": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["check", "reason"],
                "properties": { "check": { "type": "string" }, "reason": { "type": "string" } }
              }
            },
            "escalation": { "type": ["string", "null"], "description": "How many flagged spans the semantic stage re-checked, the cap, and what the reverse translation calls cost." }
          }
        }
        """;
}
