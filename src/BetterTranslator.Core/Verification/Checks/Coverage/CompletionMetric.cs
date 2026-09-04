using System.Globalization;

namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed record CompletionReport(int Translated, int Total, int ExemptUnits, int AlignmentConfidence)
{
    public double Percent => Total == 0 ? 100.0 : Math.Round(100.0 * Translated / Total, 1, MidpointRounding.AwayFromZero);

    public string PercentText => Percent.ToString("0.0", CultureInfo.InvariantCulture);

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{PercentText}% ({Translated}/{Total} units, {ExemptUnits} exempt, alignment {AlignmentConfidence})");
}

public static class CompletionMetric
{
    public static CompletionReport Compute(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var alignment = CoverageAlignment.Of(context);
        var untranslated = new HashSet<CheckRange>();

        foreach (var check in CoverageChecks())
        {
            CheckInstrumentation.Hit("completion/" + check.CheckId);
            foreach (var finding in check.Run(context))
            {
                var copied = string.Equals(finding.CheckId, Checks.CheckId.Coverage.CopyThrough, StringComparison.Ordinal);

                if (finding.SourceRange is not null && (copied || finding.Severity == CheckSeverity.Defect))
                {
                    untranslated.Add(finding.SourceRange);
                }
            }
        }

        var translatable = alignment.Pairs.Where(p => p.Translatable).ToList();
        var exempt = alignment.Pairs.Count(p => p.FullyExempt);
        var translated = translatable.Count(p => !untranslated.Contains(p.SourceRange));

        return new CompletionReport(translated, translatable.Count, exempt, alignment.Confidence);
    }

    private static IEnumerable<CoverageCheck> CoverageChecks() =>
    [
        new CopyThroughCheck(),
        new EmptyOutputCheck(),
        new DroppedUnitCheck(),
    ];
}
