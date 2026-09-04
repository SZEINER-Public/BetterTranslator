namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed class SegmentLanguageCheck : CoverageCheck
{
    private readonly SourceLanguageEvidence? _evidence;

    public SegmentLanguageCheck()
    {
    }

    public SegmentLanguageCheck(SourceLanguageEvidence evidence)
    {
        _evidence = evidence;
    }

    public override string CheckId => Checks.CheckId.Coverage.SegmentLanguage;

    private CharNgramLanguageIdentifier Identifier => (_evidence ?? CoverageServices.Default).Identifier;

    public static LanguageIdentification IdentifyVisible(CheckContext context, UnitPair pair, CharNgramLanguageIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(identifier);

        if (pair.TargetRange is null)
        {
            return LanguageIdentification.Undetermined;
        }

        var hidden = pair.TargetHidden.Concat(context.Target.Invariants.Select(i => i.Range)).ToList();
        var visible = CommandTokens.Replace(CoverageText.Visible(context.Target.Text, pair.TargetRange, hidden), m => new string(' ', m.Length));

        return visible.Count(char.IsLetter) < MinimumLetters
            ? LanguageIdentification.Undetermined
            : identifier.Identify(visible);
    }

    public const int MinimumLetters = 15;

    private static readonly System.Text.RegularExpressions.Regex CommandTokens = new(@"--?[\w-]+|<[^>\s]+>|[A-Za-z]#|\.[A-Z]{2,}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    protected override void Find(CheckContext context, CoverageAlignmentResult alignment, List<CheckFinding> findings)
    {
        var source = context.Settings.SourceLanguage;
        var target = context.Settings.TargetLanguage;

        if (string.IsNullOrWhiteSpace(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var identifier = Identifier;

        if (!identifier.Knows(source))
        {
            return;
        }

        foreach (var pair in alignment.Pairs)
        {
            if (pair.TargetRange is null || !pair.Translatable)
            {
                continue;
            }

            var identified = IdentifyVisible(context, pair, identifier);

            if (identified.IsUndetermined || !string.Equals(identified.Code, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            findings.Add(Finding(
                pair.TargetRange,
                pair.SourceRange,
                AtLeast(pair.Ceiling, CheckGranularity.Sentence),
                CheckSeverity.Defect,
                identified.Confidence,
                CauseOf(pair),
                $"unit '{pair.Identity}' reads as '{identified.Code}' rather than '{target}': '{Excerpt(pair.TargetRaw)}'"));
        }
    }
}
