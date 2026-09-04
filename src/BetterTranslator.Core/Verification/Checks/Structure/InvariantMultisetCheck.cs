namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class InvariantMultisetCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.InvariantMultiset;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        var source = context.Source.Invariants.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        var target = context.Target.Invariants.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());

        PairInflected(source, target);

        foreach (var key in source.Keys.Union(target.Keys).OrderBy(k => k.Class).ThenBy(k => k.Text, StringComparer.Ordinal))
        {
            var inSource = source.GetValueOrDefault(key) ?? [];
            var inTarget = target.GetValueOrDefault(key) ?? [];

            if (inSource.Count == inTarget.Count)
            {
                continue;
            }

            var severity = key.Class is InvariantClass.Identifier or InvariantClass.FilePath ? CheckSeverity.Defect : CheckSeverity.Score;
            var label = key.Class.ToString().ToLowerInvariant();

            if (inSource.Count > inTarget.Count)
            {
                foreach (var token in inSource.Take(inSource.Count - inTarget.Count))
                {
                    var span = SegmentTargetOf(context, token.Range);

                    findings.Add(Finding(
                        span,
                        token.Range,
                        CheckGranularity.Word,
                        severity,
                        Confidence(key.Class),
                        CauseAt(context, span, token.Range),
                        $"{label} '{key.Text}' lost: {inSource.Count} in source, {inTarget.Count} in target",
                        severity == CheckSeverity.Defect ? CheckAction.Mark : CheckAction.ScoreOnly));
                }

                continue;
            }

            foreach (var token in inTarget.Take(inTarget.Count - inSource.Count))
            {
                findings.Add(Finding(
                    token.Range,
                    null,
                    CheckGranularity.Word,
                    severity,
                    Confidence(key.Class),
                    CauseAt(context, token.Range, null),
                    $"{label} '{key.Text}' invented: {inSource.Count} in source, {inTarget.Count} in target",
                    severity == CheckSeverity.Defect ? CheckAction.Mark : CheckAction.ScoreOnly));
            }
        }
    }

    private const int InflectionLetters = 3;

    private static void PairInflected(
        Dictionary<(InvariantClass Class, string Text), List<InvariantToken>> source,
        Dictionary<(InvariantClass Class, string Text), List<InvariantToken>> target)
    {
        foreach (var key in source.Keys.Where(k => k.Class == InvariantClass.Identifier).OrderBy(k => k.Text, StringComparer.Ordinal).ToList())
        {
            var surplus = source[key].Count - (target.GetValueOrDefault(key)?.Count ?? 0);

            if (surplus <= 0)
            {
                continue;
            }

            foreach (var candidate in target.Keys
                .Where(k => k.Class == InvariantClass.Identifier && IsInflection(key.Text, k.Text))
                .OrderBy(k => k.Text, StringComparer.Ordinal)
                .ToList())
            {
                var spare = target[candidate].Count - (source.GetValueOrDefault(candidate)?.Count ?? 0);

                while (surplus > 0 && spare > 0)
                {
                    source[key].RemoveAt(source[key].Count - 1);
                    target[candidate].RemoveAt(target[candidate].Count - 1);
                    surplus--;
                    spare--;
                }
            }
        }
    }

    private static bool IsInflection(string stem, string word)
    {
        if (string.Equals(stem, word, StringComparison.Ordinal))
        {
            return false;
        }

        var shared = 0;

        while (shared < stem.Length && shared < word.Length && stem[shared] == word[shared])
        {
            shared++;
        }

        return shared >= Math.Max(3, stem.Length - 1)
            && word.Length - shared <= InflectionLetters
            && word.Skip(shared).All(char.IsLetter)
            && stem.Skip(shared).All(char.IsLetter);
    }

    private static int Confidence(InvariantClass kind) => kind switch
    {
        InvariantClass.Identifier or InvariantClass.FilePath => 95,
        InvariantClass.Url => 85,
        _ => 70,
    };

    private static (InvariantClass Class, string Text) Key(InvariantToken token) => (token.Class, token.Text);
}
