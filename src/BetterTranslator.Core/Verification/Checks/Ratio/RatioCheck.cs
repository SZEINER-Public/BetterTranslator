namespace BetterTranslator.Core.Verification.Checks.Ratio;

public abstract class RatioCheck : ICheck
{
    private readonly RatioProfile? _profile;

    protected RatioCheck()
    {
    }

    protected RatioCheck(RatioProfile profile)
    {
        _profile = profile;
    }

    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Ratio.Category;

    public RatioProfile Profile => _profile ?? RatioProfile.Default;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Profile.IsEnabled(CheckId))
        {
            return [];
        }

        var pair = RatioUnits.PairKey(context.Settings);

        if (pair.Length == 0)
        {
            return [];
        }

        var findings = new List<CheckFinding>();

        foreach (var unit in RatioUnits.Of(context))
        {
            var band = Profile.BandFor(pair, unit.UnitType);

            if (band is null)
            {
                continue;
            }

            var hit = Detect(band, unit);

            if (hit is null || hit.Confidence == 0)
            {
                continue;
            }

            findings.Add(new CheckFinding(
                CheckId,
                unit.TargetRange,
                unit.SourceRange,
                GranularityOf(unit),
                CheckSeverity.Score,
                hit.Confidence,
                CauseOf(unit),
                $"{hit.Detail}; unit '{unit.Identity}' ({unit.UnitType}, {pair}), threshold {hit.Threshold.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}",
                CheckAction.ScoreOnly));
        }

        return
        [
            .. findings
                .Where(finding => !context.Exemptions.IsExempt(finding.TargetRange))
                .OrderBy(finding => finding.TargetRange.Offset)
                .ThenBy(finding => finding.TargetRange.Length)
                .ThenBy(finding => finding.Evidence, StringComparer.Ordinal),
        ];
    }

    protected abstract RatioHit? Detect(RatioBandSet band, RatioUnit unit);

    private static CheckGranularity GranularityOf(RatioUnit unit) => unit.UnitType switch
    {
        RatioUnitType.Word => CheckGranularity.Word,
        RatioUnitType.Sentence or RatioUnitType.Cue or RatioUnitType.Value => CheckGranularity.Sentence,
        _ => CheckGranularity.Block,
    };

    private static string CauseOf(RatioUnit unit) => unit.FromTrace ? CheckCause.ModelOutput : CheckCause.AdapterRebuild;
}

public sealed class LengthRatioCheck : RatioCheck
{
    public LengthRatioCheck()
    {
    }

    public LengthRatioCheck(RatioProfile profile) : base(profile)
    {
    }

    public override string CheckId => Checks.CheckId.Ratio.LengthRatio;

    protected override RatioHit? Detect(RatioBandSet band, RatioUnit unit) =>
        RatioSignals.LengthRatio(band, unit.SourceText, unit.TargetText);
}

public sealed class TruncationCheck : RatioCheck
{
    public TruncationCheck()
    {
    }

    public TruncationCheck(RatioProfile profile) : base(profile)
    {
    }

    public override string CheckId => Checks.CheckId.Ratio.Truncation;

    protected override RatioHit? Detect(RatioBandSet band, RatioUnit unit) =>
        RatioSignals.Truncation(band, unit.SourceText, unit.TargetText);
}

public sealed class RepetitionCheck : RatioCheck
{
    public RepetitionCheck()
    {
    }

    public RepetitionCheck(RatioProfile profile) : base(profile)
    {
    }

    public override string CheckId => Checks.CheckId.Ratio.Repetition;

    protected override RatioHit? Detect(RatioBandSet band, RatioUnit unit) =>
        RatioSignals.Repetition(band, unit.TargetText);
}

public sealed class CompressionCheck : RatioCheck
{
    public CompressionCheck()
    {
    }

    public CompressionCheck(RatioProfile profile) : base(profile)
    {
    }

    public override string CheckId => Checks.CheckId.Ratio.Compression;

    protected override RatioHit? Detect(RatioBandSet band, RatioUnit unit) =>
        RatioSignals.Compression(band, unit.TargetText);
}

public sealed class InsertionCheck : RatioCheck
{
    public InsertionCheck()
    {
    }

    public InsertionCheck(RatioProfile profile) : base(profile)
    {
    }

    public override string CheckId => Checks.CheckId.Ratio.Insertion;

    protected override RatioHit? Detect(RatioBandSet band, RatioUnit unit) =>
        RatioSignals.Insertion(band, unit.SourceText, unit.TargetText);
}
