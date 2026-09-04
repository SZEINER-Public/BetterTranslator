using System.Globalization;

namespace BetterTranslator.Core.Verification.Checks.Terminology;

public abstract class TerminologyCheck : ICheck
{
    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Terminology.Category;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = TerminologyPorts.For(context);

        if (SkipReason(context, services) is not null)
        {
            return [];
        }

        var findings = new List<CheckFinding>();
        Find(context, services, TermIndex.For(context), findings);

        return
        [
            .. findings
                .Where(finding => !context.Exemptions.IsExempt(finding.TargetRange))
                .OrderBy(finding => finding.TargetRange.Offset)
                .ThenBy(finding => finding.TargetRange.Length)
                .ThenBy(finding => finding.Evidence, StringComparer.Ordinal),
        ];
    }

    public virtual string? SkipReason(CheckContext context, TerminologyServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        return services.SkipReason(context.Settings);
    }

    protected abstract void Find(CheckContext context, TerminologyServices services, TermIndex index, List<CheckFinding> findings);

    protected CheckFinding Finding(
        CheckRange target,
        CheckRange? source,
        CheckGranularity granularity,
        CheckSeverity severity,
        int confidence,
        string evidence,
        CheckAction action) =>
        new(CheckId, target, source, granularity, severity, confidence, CheckCause.ModelOutput, evidence, action);

    protected static bool Exempt(CheckContext context, TermOccurrence occurrence) =>
        context.Exemptions.IsExempt(occurrence.TargetRange)
        || context.Alignment.Any(a => a.Masks.Any(m => m.SourceRange.Contains(occurrence.SourceRange)));

    protected static string Quote(string text) => "'" + text + "'";

    protected static string Describe(TermOccurrence occurrence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{occurrence.TargetRange.UnitPath}@{occurrence.TargetRange.Offset}+{occurrence.TargetRange.Length}={(occurrence.Rendered ? occurrence.Rendering : "missing")}");
}

public sealed class AcceptedRenderingCheck : TerminologyCheck
{
    public override string CheckId => Checks.CheckId.Terminology.AcceptedRendering;

    protected override void Find(CheckContext context, TerminologyServices services, TermIndex index, List<CheckFinding> findings)
    {
        foreach (var term in index.Terms.Where(t => t.Glossary is not null))
        {
            foreach (var occurrence in term.Occurrences.Where(o => o.Kind is RenderingKind.Inferred or RenderingKind.Missing))
            {
                if (Exempt(context, occurrence))
                {
                    continue;
                }

                var found = occurrence.Rendered ? "accepted rendering absent; nearest candidate " + Quote(occurrence.Rendering) : "accepted rendering absent";

                findings.Add(Finding(
                    occurrence.UnitTargetRange,
                    occurrence.SourceRange,
                    CheckGranularity.Sentence,
                    CheckSeverity.Defect,
                    occurrence.Kind == RenderingKind.Inferred ? 80 : 70,
                    "term " + Quote(term.SourceTerm) + " expects " + Quote(term.Glossary!.Accepted) + "; " + found,
                    CheckAction.Repair));
            }
        }
    }
}

public sealed class RejectedRenderingCheck : TerminologyCheck
{
    public override string CheckId => Checks.CheckId.Terminology.RejectedRendering;

    protected override void Find(CheckContext context, TerminologyServices services, TermIndex index, List<CheckFinding> findings)
    {
        foreach (var term in index.Terms.Where(t => t.Glossary is not null))
        {
            foreach (var occurrence in term.Occurrences.Where(o => o.Kind == RenderingKind.Rejected))
            {
                if (Exempt(context, occurrence))
                {
                    continue;
                }

                findings.Add(Finding(
                    occurrence.TargetRange,
                    occurrence.SourceRange,
                    CheckGranularity.Word,
                    CheckSeverity.Defect,
                    95,
                    "term " + Quote(term.SourceTerm) + " rendered as rejected " + Quote(occurrence.Lemma) + " (" + occurrence.Rendering + "); accepted " + Quote(term.Glossary!.Accepted),
                    CheckAction.Repair));
            }
        }
    }
}

public sealed class RunConsistencyCheck : TerminologyCheck
{
    public const int AdvisoryMinimumOccurrences = 3;

    public override string CheckId => Checks.CheckId.Terminology.RunConsistency;

    protected override void Find(CheckContext context, TerminologyServices services, TermIndex index, List<CheckFinding> findings)
    {
        foreach (var term in index.Terms)
        {
            var occurrences = term.Occurrences.Where(o => o.Rendered && !Exempt(context, o)).ToList();
            var lemmas = occurrences.Select(o => o.Lemma).Distinct(StringComparer.Ordinal).Count();

            if (lemmas < 2)
            {
                continue;
            }

            if (term.Glossary is null && occurrences.Count < AdvisoryMinimumOccurrences)
            {
                continue;
            }

            var filtered = term with { Occurrences = occurrences };
            var evidence = Evidence(filtered);
            var sourceRange = occurrences[0].SourceRange;

            if (term.Glossary is null)
            {
                findings.Add(Finding(
                    context.Target.Whole,
                    sourceRange,
                    CheckGranularity.Document,
                    CheckSeverity.Advisory,
                    Confidence(occurrences, 60),
                    evidence + "; propose glossary entry " + Quote(term.SourceTerm) + " = " + Quote(filtered.MajorityRendering),
                    CheckAction.Mark));
                continue;
            }

            findings.Add(Finding(
                context.Target.Whole,
                sourceRange,
                CheckGranularity.Document,
                CheckSeverity.Defect,
                Confidence(occurrences, 90),
                evidence,
                CheckAction.Mark));
        }
    }

    private static int Confidence(IReadOnlyList<TermOccurrence> occurrences, int ceiling)
    {
        var inferred = occurrences.Count(o => o.Kind == RenderingKind.Inferred);
        var penalty = (int)Math.Round(30.0 * inferred / occurrences.Count, MidpointRounding.AwayFromZero);

        return Math.Max(40, ceiling - penalty);
    }

    private static string Evidence(TermEntry term)
    {
        var occurrences = term.Occurrences;
        var distinct = term.DistinctLemmas.Count;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"term {Quote(term.SourceTerm)} rendered {distinct} ways across {occurrences.Count} occurrences; majority {Quote(term.MajorityRendering)} ({term.MajorityCount}/{occurrences.Count}); occurrences: {string.Join("; ", occurrences.Select(Describe))}");
    }
}
