using System.Globalization;

namespace BetterTranslator.Core.Verification.Checks.Semantics;

public abstract class SemanticCheck : ICheck
{
    public abstract string CheckId { get; }

    public string Category => Checks.CheckId.Semantics.Category;

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var admitted = EscalationGate.AdmittedFor(context);

        if (admitted.Count == 0)
        {
            return [];
        }

        var services = SemanticPorts.For(context);

        if (SkipReason(services) is not null)
        {
            return [];
        }

        var pair = LanguagePair(context.Settings);
        var findings = new List<CheckFinding>();

        foreach (var span in admitted.OrderBy(s => s.TargetRange.Offset).ThenBy(s => s.TargetRange.Length))
        {
            var key = SemanticCache.Key(span.SourceText, span.TargetText, ModelIdentity(services));
            Find(context, services, span, key, pair, findings);
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

    public abstract string? SkipReason(SemanticServices services);

    protected abstract string ModelIdentity(SemanticServices services);

    protected abstract void Find(CheckContext context, SemanticServices services, AdmittedSpan span, string cacheKey, (string Source, string Target) pair, List<CheckFinding> findings);

    protected CheckFinding Finding(AdmittedSpan span, int confidence, string evidence) =>
        new(
            CheckId,
            span.TargetRange,
            span.SourceRange,
            GranularityOf(span),
            CheckSeverity.Score,
            confidence,
            CheckCause.ModelOutput,
            evidence + $"; escalated by {span.OriginatingCheck} at confidence {span.OriginatingConfidence}",
            CheckAction.ScoreOnly);

    protected static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    protected static (string Source, string Target) LanguagePair(CheckRunSettings settings) =>
        (settings.SourceLanguage, settings.TargetLanguage);

    protected static double? Similarity(SemanticServices services, string cacheKey, string source, string target)
    {
        if (services.Cache.TryGet(cacheKey, out var cached) && cached.Similarity is not null)
        {
            return cached.Similarity;
        }

        var a = services.Embeddings.Embed(source);
        var b = services.Embeddings.Embed(target);

        if (a is null || b is null)
        {
            return null;
        }

        var similarity = EmbeddingMath.Cosine(a, b);
        services.Cache.Set(cacheKey, services.Cache.Get(cacheKey) with { Similarity = similarity });

        return similarity;
    }

    protected static string? Reverse(CheckContext context, SemanticServices services, AdmittedSpan span, string cacheKey, (string Source, string Target) pair)
    {
        if (services.Cache.TryGet(cacheKey, out var cached) && cached.Reverse is not null)
        {
            return cached.Reverse;
        }

        var text = ReverseInput(context, services.Settings, span);
        var reverse = services.Reverse.Translate(text, pair.Target, pair.Source);

        if (reverse is null)
        {
            return null;
        }

        services.Cache.Set(cacheKey, services.Cache.Get(cacheKey) with { Reverse = reverse });

        return reverse;
    }

    protected static string ReverseInput(CheckContext context, SemanticSettings settings, AdmittedSpan span)
    {
        var segment = context.Alignment.FirstOrDefault(s => s.TargetRange is not null && s.TargetRange.Contains(span.TargetRange));

        if (segment?.TargetRange is null || segment.TargetRange.Length == 0)
        {
            return span.TargetText;
        }

        var share = (double)span.TargetRange.Length / segment.TargetRange.Length;

        if (share < settings.WholeUnitShare)
        {
            return span.TargetText;
        }

        var text = context.Target.Text;
        var offset = Math.Clamp(segment.TargetRange.Offset, 0, text.Length);
        var length = Math.Clamp(segment.TargetRange.Length, 0, text.Length - offset);

        return text.Substring(offset, length);
    }

    private static CheckGranularity GranularityOf(AdmittedSpan span)
    {
        var words = span.TargetText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        return words <= 1 ? CheckGranularity.Word : CheckGranularity.Sentence;
    }
}

public sealed class EmbeddingSimilarityCheck : SemanticCheck
{
    public override string CheckId => Checks.CheckId.Semantics.EmbeddingSimilarity;

    public override string? SkipReason(SemanticServices services) =>
        services.Embeddings.Available ? null : "embedding model unavailable: " + services.Embeddings.UnavailableReason;

    protected override string ModelIdentity(SemanticServices services) => services.Embeddings.ModelIdentity;

    protected override void Find(CheckContext context, SemanticServices services, AdmittedSpan span, string cacheKey, (string Source, string Target) pair, List<CheckFinding> findings)
    {
        var similarity = Similarity(services, cacheKey, span.SourceText, span.TargetText);

        if (similarity is null)
        {
            return;
        }

        findings.Add(Finding(
            span,
            EmbeddingMath.SimilarityConfidence(similarity.Value),
            $"cross-lingual similarity {Number(similarity.Value)} ({services.Embeddings.ModelIdentity})"));
    }
}

public sealed class ReverseTranslationCheck : SemanticCheck
{
    public override string CheckId => Checks.CheckId.Semantics.ReverseTranslation;

    public override string? SkipReason(SemanticServices services) =>
        services.Reverse.Available ? null : "reverse translation unavailable: " + services.Reverse.UnavailableReason;

    protected override string ModelIdentity(SemanticServices services) => services.Reverse.ModelIdentity;

    protected override void Find(CheckContext context, SemanticServices services, AdmittedSpan span, string cacheKey, (string Source, string Target) pair, List<CheckFinding> findings)
    {
        var reverse = Reverse(context, services, span, cacheKey, pair);

        if (reverse is null)
        {
            return;
        }

        var overlap = ContentTokens.Overlap(span.SourceText, reverse);

        findings.Add(Finding(
            span,
            (int)Math.Clamp(Math.Round(100 * (1 - overlap), MidpointRounding.AwayFromZero), 0, 100),
            $"reverse translation '{Excerpt(reverse)}' with content token overlap {Number(overlap)} ({services.Reverse.ModelIdentity})"));
    }

    private static string Excerpt(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 60 ? flat : flat[..57] + "...";
    }
}

public sealed class ReverseComparisonCheck : SemanticCheck
{
    public override string CheckId => Checks.CheckId.Semantics.ReverseComparison;

    public override string? SkipReason(SemanticServices services)
    {
        if (!services.Reverse.Available)
        {
            return "reverse translation unavailable: " + services.Reverse.UnavailableReason;
        }

        return services.Embeddings.Available ? null : "embedding model unavailable: " + services.Embeddings.UnavailableReason;
    }

    protected override string ModelIdentity(SemanticServices services) => services.Reverse.ModelIdentity;

    protected override void Find(CheckContext context, SemanticServices services, AdmittedSpan span, string cacheKey, (string Source, string Target) pair, List<CheckFinding> findings)
    {
        var reverse = Reverse(context, services, span, cacheKey, pair);

        if (reverse is null)
        {
            return;
        }

        var cached = services.Cache.Get(cacheKey);
        var similarity = cached.ReverseSimilarity;
        var overlap = cached.TokenOverlap;

        if (similarity is null)
        {
            var reverseKey = SemanticCache.Key(span.SourceText, reverse, services.Embeddings.ModelIdentity);
            similarity = Similarity(services, reverseKey, span.SourceText, reverse);

            if (similarity is null)
            {
                return;
            }
        }

        overlap ??= ContentTokens.Overlap(span.SourceText, reverse);
        services.Cache.Set(cacheKey, cached with { Reverse = reverse, ReverseSimilarity = similarity, TokenOverlap = overlap });

        var combined = Combine(similarity.Value, overlap.Value);

        findings.Add(Finding(
            span,
            combined,
            $"reverse similarity {Number(similarity.Value)}, content token overlap {Number(overlap.Value)}, combined confidence {combined}; carried by {(1 - Math.Max(0, similarity.Value) >= 1 - overlap.Value ? "similarity" : "overlap")}"));
    }

    public static int Combine(double similarity, double overlap)
    {
        var fromSimilarity = 1 - Math.Max(0, similarity);
        var fromOverlap = 1 - Math.Clamp(overlap, 0, 1);
        var combined = 0.5 * fromSimilarity + 0.5 * fromOverlap;

        return (int)Math.Clamp(Math.Round(100 * combined, MidpointRounding.AwayFromZero), 0, 100);
    }
}

public static class ContentTokens
{
    public static IReadOnlySet<string> Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var inside = i < text.Length && char.IsLetterOrDigit(text[i]);

            if (inside && start < 0)
            {
                start = i;
            }
            else if (!inside && start >= 0)
            {
                var token = text[start..i].ToLowerInvariant();

                if (token.Length >= 3)
                {
                    tokens.Add(token);
                }

                start = -1;
            }
        }

        return tokens;
    }

    public static double Overlap(string a, string b)
    {
        var left = Of(a);
        var right = Of(b);

        if (left.Count == 0 && right.Count == 0)
        {
            return 1;
        }

        var shared = left.Intersect(right, StringComparer.Ordinal).Count();
        var union = left.Union(right, StringComparer.Ordinal).Count();

        return union == 0 ? 0 : (double)shared / union;
    }
}
