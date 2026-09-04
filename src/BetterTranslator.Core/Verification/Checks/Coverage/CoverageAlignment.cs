using System.Runtime.CompilerServices;

namespace BetterTranslator.Core.Verification.Checks.Coverage;

public sealed record TokenPair(CoverageToken Source, CoverageToken Target, bool Identical);

public sealed record UnitPair(
    string Identity,
    CheckRange SourceRange,
    CheckRange? TargetRange,
    SegmentOutcome Outcome,
    int Confidence,
    CheckGranularity Ceiling,
    IReadOnlyList<TokenPair> Anchors,
    IReadOnlyList<CoverageToken> SourceTokens,
    IReadOnlyList<CoverageToken> TargetTokens,
    IReadOnlyList<CheckRange> SourceHidden,
    IReadOnlyList<CheckRange> TargetHidden,
    string SourceNormalized,
    string TargetNormalized,
    bool FromTrace)
{
    public string SourceRaw { get; init; } = string.Empty;

    public string TargetRaw { get; init; } = string.Empty;

    public bool HasTarget => TargetRange is not null;

    public bool Translatable => CoverageText.HasLetter(SourceNormalized);

    public bool FullyExempt => !Translatable && !CoverageText.IsBlank(SourceRaw);
}

public sealed record CoverageAlignmentResult(IReadOnlyList<UnitPair> Pairs, int Confidence, bool FromTrace);

public static class CoverageAlignment
{
    public const int WordCeilingFloor = 75;

    public const int SentenceCeilingFloor = 45;

    private const double UnitMatchFloor = 0.2;

    private static readonly ConditionalWeakTable<CheckContext, CoverageAlignmentResult> Cache = new();

    public static CoverageAlignmentResult Of(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Cache.GetValue(context, Build);
    }

    public static CoverageAlignmentResult Build(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var pairs = context.Alignment.Count > 0 ? FromTraces(context) : FromChunks(context);
        var scored = pairs.Where(p => p.Translatable).Select(p => p.Confidence).ToList();
        var confidence = scored.Count == 0 ? 0 : (int)Math.Round(scored.Average(), MidpointRounding.AwayFromZero);

        return new CoverageAlignmentResult(pairs, confidence, context.Alignment.Count > 0);
    }

    public static CheckGranularity CeilingFor(int confidence) =>
        confidence >= WordCeilingFloor ? CheckGranularity.Word
        : confidence >= SentenceCeilingFloor ? CheckGranularity.Sentence
        : CheckGranularity.Block;

    private static List<UnitPair> FromTraces(CheckContext context)
    {
        var pairs = new List<UnitPair>();

        foreach (var segment in context.Alignment.OrderBy(s => s.SourceRange.Offset).ThenBy(s => s.Identity, StringComparer.Ordinal))
        {
            var sourceHidden = segment.Masks.Where(m => m.SourceLocated).Select(m => m.SourceRange).ToList();
            var targetHidden = segment.Masks.Where(m => m.TargetRange is not null).Select(m => m.TargetRange!).ToList();

            var targetRange = segment.Outcome == SegmentOutcome.Dropped ? null : segment.TargetRange;

            pairs.Add(Pair(context, segment.Identity, segment.SourceRange, targetRange, segment.Outcome, sourceHidden, targetHidden, true, 100));
        }

        return pairs;
    }

    private static List<UnitPair> FromChunks(CheckContext context)
    {
        var source = Units(context.Source);
        var target = Units(context.Target);
        var sourceTokens = source.Select(u => CoverageTokenizer.Tokenize(context.Source.Text, u.Range)).ToList();
        var targetTokens = target.Select(u => CoverageTokenizer.Tokenize(context.Target.Text, u.Range)).ToList();
        var matched = MatchUnits(sourceTokens, targetTokens);
        var pairs = new List<UnitPair>();

        for (var i = 0; i < source.Count; i++)
        {
            var (targetIndex, similarity) = matched[i];
            var targetRange = targetIndex >= 0 ? target[targetIndex].Range : null;
            var identity = source[i].Identity + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var outcome = targetIndex >= 0 ? SegmentOutcome.Translated : SegmentOutcome.Dropped;
            var cap = targetIndex >= 0 ? (int)Math.Round(60 + 40 * similarity, MidpointRounding.AwayFromZero) : 100;

            pairs.Add(Pair(context, identity, source[i].Range, targetRange, outcome, [], [], false, cap));
        }

        return pairs;
    }

    private static List<ChunkNode> Units(DocumentModel model)
    {
        var chunks = model.Chunks.Where(c => !CoverageText.IsBlank(CoverageText.Slice(model, c.Range))).ToList();

        if (chunks.Count > 0)
        {
            return chunks;
        }

        return CoverageText.IsBlank(model.Text) ? [] : [new ChunkNode("document", "document", model.Whole)];
    }

    private static (int TargetIndex, double Similarity)[] MatchUnits(
        List<IReadOnlyList<CoverageToken>> source,
        List<IReadOnlyList<CoverageToken>> target)
    {
        var n = source.Count;
        var m = target.Count;
        var result = new (int, double)[n];
        Array.Fill(result, (-1, 0.0));

        if (n == 0 || m == 0)
        {
            return result;
        }

        if (n == m)
        {
            for (var i = 0; i < n; i++)
            {
                result[i] = (i, Similarity(source[i], target[i]));
            }

            return result;
        }

        var sim = new double[n, m];

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                sim[i, j] = Similarity(source[i], target[j]);
            }
        }

        var score = new double[n + 1, m + 1];

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var take = sim[i - 1, j - 1] >= UnitMatchFloor ? score[i - 1, j - 1] + sim[i - 1, j - 1] : double.NegativeInfinity;
                score[i, j] = Math.Max(Math.Max(score[i - 1, j], score[i, j - 1]), take);
            }
        }

        var si = n;
        var tj = m;

        while (si > 0 && tj > 0)
        {
            var take = sim[si - 1, tj - 1] >= UnitMatchFloor ? score[si - 1, tj - 1] + sim[si - 1, tj - 1] : double.NegativeInfinity;

            if (score[si, tj] == take)
            {
                result[si - 1] = (tj - 1, sim[si - 1, tj - 1]);
                si--;
                tj--;
            }
            else if (score[si, tj] == score[si - 1, tj])
            {
                si--;
            }
            else
            {
                tj--;
            }
        }

        return result;
    }

    private static double Similarity(IReadOnlyList<CoverageToken> source, IReadOnlyList<CoverageToken> target)
    {
        var sourceKeys = source.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var targetKeys = target.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var shared = sourceKeys.Intersect(targetKeys, StringComparer.Ordinal).Count();
        var union = sourceKeys.Union(targetKeys, StringComparer.Ordinal).Count();
        var overlap = union == 0 ? 1.0 : (double)shared / union;
        var sourceLength = source.Sum(t => t.Text.Length);
        var targetLength = target.Sum(t => t.Text.Length);
        var longer = Math.Max(sourceLength, targetLength);
        var lengthRatio = longer == 0 ? 1.0 : (double)Math.Min(sourceLength, targetLength) / longer;

        return 0.5 * overlap + 0.5 * lengthRatio;
    }

    private static UnitPair Pair(
        CheckContext context,
        string identity,
        CheckRange sourceRange,
        CheckRange? targetRange,
        SegmentOutcome outcome,
        List<CheckRange> sourceHidden,
        List<CheckRange> targetHidden,
        bool fromTrace,
        int cap)
    {
        var sourceRaw = CoverageText.Slice(context.Source, sourceRange);
        var targetRaw = targetRange is null ? string.Empty : CoverageText.Slice(context.Target, targetRange);
        var exemptOnTarget = targetRange is null
            ? []
            : ExemptionFilter.HonoredSpans(context).Where(s => s.Range.Overlaps(targetRange)).ToList();

        var maskTargets = targetHidden.ToList();

        foreach (var span in exemptOnTarget)
        {
            targetHidden.Add(span.Range);
        }

        var sourceVisible = CoverageText.Visible(context.Source.Text, sourceRange, sourceHidden);
        sourceVisible = CoverageText.RemoveTexts(sourceVisible, exemptOnTarget.Where(s => !maskTargets.Contains(s.Range)).Select(s => s.Text));
        var targetVisible = targetRange is null ? string.Empty : CoverageText.Visible(context.Target.Text, targetRange, targetHidden);

        var sourceTokens = CoverageTokenizer.Tokenize(context.Source.Text, sourceRange);
        var targetTokens = targetRange is null ? [] : CoverageTokenizer.Tokenize(context.Target.Text, targetRange);

        var (anchors, confidence) = targetRange is null
            ? (new List<TokenPair>(), 100)
            : AlignTokens(sourceTokens, targetTokens, sourceHidden, targetHidden);

        confidence = Math.Min(confidence, cap);
        var ceiling = targetRange is null ? CheckGranularity.Block : CeilingFor(confidence);

        return new UnitPair(
            identity,
            sourceRange,
            targetRange,
            outcome,
            confidence,
            ceiling,
            anchors,
            sourceTokens,
            targetTokens,
            sourceHidden,
            targetHidden,
            CoverageText.Normalize(sourceVisible),
            CoverageText.Normalize(targetVisible),
            fromTrace)
        {
            SourceRaw = sourceRaw,
            TargetRaw = targetRaw,
        };
    }

    private static (List<TokenPair> Anchors, int Confidence) AlignTokens(
        IReadOnlyList<CoverageToken> source,
        IReadOnlyList<CoverageToken> target,
        IReadOnlyList<CheckRange> sourceHidden,
        IReadOnlyList<CheckRange> targetHidden)
    {
        var targetWords = target.Where(t => t.Kind == CoverageTokenKind.Word).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var sourceWords = source.Where(t => t.Kind == CoverageTokenKind.Word).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

        var sourceAnchors = source
            .Select((t, i) => (Token: t, Index: i, Key: AnchorKey(t, targetWords, sourceHidden)))
            .Where(a => a.Key is not null)
            .ToList();
        var targetAnchors = target
            .Select((t, i) => (Token: t, Index: i, Key: AnchorKey(t, sourceWords, targetHidden)))
            .Where(a => a.Key is not null)
            .ToList();

        var total = Math.Max(sourceAnchors.Count, targetAnchors.Count);

        if (total == 0)
        {
            return ([], 55);
        }

        var matches = LongestCommonSubsequence([.. sourceAnchors.Select(a => a.Key!)], [.. targetAnchors.Select(a => a.Key!)]);
        var anchors = matches
            .Select(m => new TokenPair(
                sourceAnchors[m.Source].Token,
                targetAnchors[m.Target].Token,
                sourceAnchors[m.Source].Token.Kind == CoverageTokenKind.Word))
            .ToList();

        var matchedKeys = matches.Select(m => sourceAnchors[m.Source].Key!).ToList();
        var targetKeys = targetAnchors.Select(a => a.Key!).ToList();
        var outOfOrder = 0;

        foreach (var group in sourceAnchors.Select(a => a.Key!).GroupBy(k => k, StringComparer.Ordinal))
        {
            var inTarget = targetKeys.Count(k => string.Equals(k, group.Key, StringComparison.Ordinal));
            var matched = matchedKeys.Count(k => string.Equals(k, group.Key, StringComparison.Ordinal));
            outOfOrder += Math.Max(0, Math.Min(group.Count(), inTarget) - matched);
        }

        var intervals = matches.Select(m => (sourceAnchors[m.Source].Index, targetAnchors[m.Target].Index)).ToList();
        var confidence = (int)Math.Round(100.0 * matches.Count / total, MidpointRounding.AwayFromZero);
        confidence -= 15 * outOfOrder;
        confidence -= 10 * UnbalancedIntervals(source, target, intervals);

        return (anchors, Math.Clamp(confidence, 0, 100));
    }

    private static int UnbalancedIntervals(
        IReadOnlyList<CoverageToken> source,
        IReadOnlyList<CoverageToken> target,
        List<(int SourceIndex, int TargetIndex)> matched)
    {
        var unbalanced = 0;
        var previousSource = -1;
        var previousTarget = -1;

        foreach (var (s, t) in matched.Append((source.Count, target.Count)))
        {
            var sourceWords = CountWords(source, previousSource + 1, s);
            var targetWords = CountWords(target, previousTarget + 1, t);
            var larger = Math.Max(sourceWords, targetWords);

            if (larger >= 3 && Math.Abs(sourceWords - targetWords) > Math.Max(2, larger / 2))
            {
                unbalanced++;
            }

            previousSource = s;
            previousTarget = t;
        }

        return unbalanced;
    }

    private static int CountWords(IReadOnlyList<CoverageToken> tokens, int from, int to)
    {
        var count = 0;

        for (var i = Math.Max(0, from); i < Math.Min(to, tokens.Count); i++)
        {
            if (tokens[i].Kind == CoverageTokenKind.Word)
            {
                count++;
            }
        }

        return count;
    }

    private static string? AnchorKey(CoverageToken token, HashSet<string> otherWords, IReadOnlyList<CheckRange> hidden)
    {
        if (hidden.Any(h => h.Contains(token.Range)))
        {
            return "m:" + token.Key;
        }

        return token.Kind switch
        {
            CoverageTokenKind.Number => "n:" + token.Text,
            CoverageTokenKind.Punctuation => "p:" + token.Text,
            _ => otherWords.Contains(token.Key) ? "w:" + token.Key : null,
        };
    }

    private static List<(int Source, int Target)> LongestCommonSubsequence(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];

        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var result = new List<(int, int)>();
        var x = 0;
        var y = 0;

        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                result.Add((x, y));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return result;
    }
}
