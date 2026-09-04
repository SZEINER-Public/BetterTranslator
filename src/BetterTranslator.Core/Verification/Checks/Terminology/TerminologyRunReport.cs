using System.Globalization;

namespace BetterTranslator.Core.Verification.Checks.Terminology;

public enum TerminologyCheckState
{
    Ran,
    Skipped,
}

public sealed record TerminologyCheckStatus(string CheckId, TerminologyCheckState State, string Reason, int Findings)
{
    public bool CountsTowardPassTotal => State == TerminologyCheckState.Ran;
}

public sealed record TerminologyRunReport(
    IReadOnlyList<TerminologyCheckStatus> Checks,
    IReadOnlyList<CheckFinding> Findings,
    IReadOnlyList<TermEntry> Terms)
{
    public int Ran => Checks.Count(c => c.State == TerminologyCheckState.Ran);

    public int Skipped => Checks.Count(c => c.State == TerminologyCheckState.Skipped);

    public string Summary()
    {
        var renderings = string.Join(", ", Terms.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.SourceTerm}={t.DistinctLemmas.Count}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Ran} ran, {Skipped} skipped, {Findings.Count} findings; distinct renderings per term: {renderings}");
    }

    public static TerminologyRunReport Build(CheckContext context, IEnumerable<TerminologyCheck>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = TerminologyPorts.For(context);
        var statuses = new List<TerminologyCheckStatus>();
        var findings = new List<CheckFinding>();

        foreach (var check in checks ?? Defaults())
        {
            var reason = check.SkipReason(context, services);

            if (reason is not null)
            {
                statuses.Add(new TerminologyCheckStatus(check.CheckId, TerminologyCheckState.Skipped, reason, 0));
                continue;
            }

            var produced = check.Run(context);
            findings.AddRange(produced);
            statuses.Add(new TerminologyCheckStatus(check.CheckId, TerminologyCheckState.Ran, string.Empty, produced.Count));
        }

        var terms = services.SkipReason(context.Settings) is null ? TermIndex.For(context).Terms : [];

        return new TerminologyRunReport(statuses, findings, terms);
    }

    private static IEnumerable<TerminologyCheck> Defaults() =>
    [
        new AcceptedRenderingCheck(),
        new RejectedRenderingCheck(),
        new RunConsistencyCheck(),
    ];
}
