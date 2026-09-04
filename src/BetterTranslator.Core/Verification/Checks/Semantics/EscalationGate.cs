using System.Runtime.CompilerServices;

namespace BetterTranslator.Core.Verification.Checks.Semantics;

public sealed record AdmittedSpan(
    CheckRange TargetRange,
    CheckRange SourceRange,
    string SourceText,
    string TargetText,
    string OriginatingCheck,
    int OriginatingConfidence,
    string Identity);

public sealed record EscalationDecision(IReadOnlyList<AdmittedSpan> Admitted, int Flagged, int Refused, int Cap);

public static class EscalationGate
{
    private static readonly ConditionalWeakTable<CheckContext, EscalationDecision> Decisions = new();

    private static readonly string[] EscalatingCategories =
    [
        CheckId.Coverage.Category,
        CheckId.Ratio.Category,
        CheckId.Runtime.Category,
    ];

    public static EscalationDecision Admit(CheckContext context, IReadOnlyList<CheckFinding> priorFindings, int cap)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(priorFindings);

        var candidates = priorFindings
            .Where(f => EscalatingCategories.Any(c => f.CheckId.StartsWith(c + "-", StringComparison.Ordinal)))
            .Where(f => f.TargetRange.Length > 0 && f.TargetRange.End <= context.Target.Length)
            .Where(f => !context.Exemptions.IsExempt(f.TargetRange))
            .OrderByDescending(f => f.Confidence)
            .ThenBy(f => f.TargetRange.Offset)
            .ThenBy(f => f.TargetRange.Length)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ToList();

        var admitted = new List<AdmittedSpan>();
        var seen = new HashSet<CheckRange>();

        foreach (var finding in candidates)
        {
            if (!seen.Add(finding.TargetRange))
            {
                continue;
            }

            if (admitted.Count >= Math.Max(0, cap))
            {
                break;
            }

            var sourceRange = finding.SourceRange ?? SourceFor(context, finding.TargetRange);

            if (sourceRange is null)
            {
                continue;
            }

            var sourceText = Slice(context.Source.Text, sourceRange);
            var targetText = Slice(context.Target.Text, finding.TargetRange);

            if (sourceText.Trim().Length == 0 || targetText.Trim().Length == 0)
            {
                continue;
            }

            admitted.Add(new AdmittedSpan(
                finding.TargetRange,
                sourceRange,
                sourceText,
                targetText,
                finding.CheckId,
                finding.Confidence,
                finding.TargetRange.UnitPath + "@" + finding.TargetRange.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var decision = new EscalationDecision(admitted, seen.Count, seen.Count - admitted.Count, cap);
        Decisions.AddOrUpdate(context, decision);

        return decision;
    }

    public static EscalationDecision? DecisionFor(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Decisions.TryGetValue(context, out var decision) ? decision : null;
    }

    public static IReadOnlyList<AdmittedSpan> AdmittedFor(CheckContext context) => DecisionFor(context)?.Admitted ?? [];

    private static CheckRange? SourceFor(CheckContext context, CheckRange target)
    {
        var segment = context.Alignment.FirstOrDefault(s => s.TargetRange is not null && s.TargetRange.Contains(target));
        return segment?.SourceRange;
    }

    private static string Slice(string text, CheckRange range)
    {
        var offset = Math.Clamp(range.Offset, 0, text.Length);
        var length = Math.Clamp(range.Length, 0, text.Length - offset);

        return text.Substring(offset, length);
    }
}
