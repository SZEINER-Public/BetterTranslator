using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Core.Verification.Checks.Ratio;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public sealed record NaturalnessSample(string SourceLanguage, string TargetLanguage, string Source, string Target);

public static class NaturalnessCalibration
{
    public const int MinimumTokens = 6;

    public static NaturalnessProfile Calibrate(IReadOnlyList<NaturalnessSample> samples, Func<string, string, CheckContext> contextFor, ITagger tagger, NaturalnessPack? pack, int improvementMargin = 1)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(contextFor);
        ArgumentNullException.ThrowIfNull(tagger);

        var pairs = new List<NaturalnessPairProfile>();

        foreach (var group in samples.GroupBy(s => NaturalnessProfile.Key(s.SourceLanguage, s.TargetLanguage), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var unit in new[] { NaturalnessProfile.UnitSentence, NaturalnessProfile.UnitBlock })
            {
                var crossing = new List<double>();
                var excess = new List<double>();
                var reference = new Dictionary<string, int>(StringComparer.Ordinal);
                var tagged = 0;

                foreach (var sample in group)
                {
                    var sampleUnit = RatioMeasures.SentenceCount(sample.Source) <= 1 ? NaturalnessProfile.UnitSentence : NaturalnessProfile.UnitBlock;

                    if (!string.Equals(sampleUnit, unit, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var context = contextFor(sample.Source, sample.Target);
                    var pair = CoverageAlignment.Of(context).Pairs.FirstOrDefault(p => p.HasTarget && p.Translatable);

                    if (pair is null)
                    {
                        continue;
                    }

                    var measure = AlignmentCrossing.Compute(pair);

                    if (measure.SourceWords >= MinimumTokens && measure.Matched >= 3)
                    {
                        crossing.Add(measure.Normalized);
                    }

                    excess.Add(Math.Abs(SentenceBoundaryFit.Compute(pair).Excess));

                    if (tagger.Available)
                    {
                        var tags = TagSequences.TagsOf(tagger, pair.TargetTokens.Where(t => t.Kind == CoverageTokenKind.Word));

                        if (tags.All(t => t != "unknown") && tags.Count >= MinimumTokens)
                        {
                            foreach (var (key, count) in TagSequences.Bigrams(tags))
                            {
                                reference[key] = reference.GetValueOrDefault(key) + count;
                            }

                            tagged++;
                        }
                    }
                }

                pairs.Add(new NaturalnessPairProfile(
                    group.Key,
                    unit,
                    new NaturalnessBand(crossing.Count == 0 ? 0 : Math.Round(crossing.Min(), 3), crossing.Count, MinimumTokens),
                    new NaturalnessBand(tagged == 0 ? 0 : 0.5, tagged, MinimumTokens),
                    new NaturalnessBand(excess.Count == 0 ? 0 : excess.Max(), excess.Count, 1),
                    reference));
            }
        }

        return new NaturalnessProfile
        {
            Source = "calibrated from the committed parallel fixture by NaturalnessCalibration",
            ImprovementMargin = improvementMargin,
            Pairs = pairs,
        };
    }
}
