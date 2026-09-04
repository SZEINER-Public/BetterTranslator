using System.Text.RegularExpressions;

namespace BetterTranslator.Core.Verification.Checks.Ratio;

public sealed record ParallelSample(string Pair, string Unit, string Source, string Target, string Origin);

public sealed record CalibrationSettings(
    double PrecisionFloor,
    double BandQuantile,
    int HeldOutEvery,
    double ScoreWeight);

public static partial class RatioCalibration
{
    public const string NegativeRecipe =
        "held-out positives are the committed pairs; negatives are derived from each held-out pair: "
        + "RAT-101 target doubled and target cut to its first third; RAT-102 target cut to sixty percent at a word boundary with the terminal mark removed; "
        + "RAT-103 last two words repeated six more times; RAT-104 a three word phrase appended eight times; "
        + "RAT-105 a translator preamble prepended and three meta sentences appended";

    [GeneratedRegex(@"^\|\s*(?<en>[^|]+?)\s*\|\s*(?<cs>[^|]+?)\s*\|")]
    private static partial Regex TableRow();

    public static bool IsHeldOut(ParallelSample sample, int heldOutEvery)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return heldOutEvery > 0 && Fnv1a(sample.Source + "" + sample.Target) % (uint)heldOutEvery == 0;
    }

    public static IReadOnlyList<ParallelSample> TermPairsFromTable(string markdown, string pair, string origin)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var samples = new List<ParallelSample>();

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = TableRow().Match(line);

            if (!match.Success)
            {
                continue;
            }

            var en = match.Groups["en"].Value.Trim();
            var cs = match.Groups["cs"].Value.Trim();

            if (en.Length == 0 || cs.Length == 0 || cs == "-" || en.All(c => c == '-') || IsHeader(en))
            {
                continue;
            }

            if (!en.Any(char.IsLetter) || !cs.Any(char.IsLetter) || cs.Contains('\\') || cs.Contains('('))
            {
                continue;
            }

            samples.Add(new ParallelSample(pair, RatioUnitType.Word, en, cs, origin));
        }

        return samples;
    }

    public static RatioProfile Calibrate(
        IReadOnlyList<ParallelSample> samples,
        CalibrationSettings settings,
        IReadOnlyList<RatioSource> sources)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sources);

        var calibration = samples.Where(s => !IsHeldOut(s, settings.HeldOutEvery)).ToList();
        var heldOut = samples.Where(s => IsHeldOut(s, settings.HeldOutEvery)).ToList();

        var bands = calibration
            .GroupBy(s => (s.Pair, s.Unit))
            .OrderBy(g => g.Key.Pair, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Unit, StringComparer.Ordinal)
            .Select(g => Band(g.Key.Pair, g.Key.Unit, [.. g], settings.BandQuantile))
            .ToList();

        var checks = new[]
        {
            CheckId.Ratio.LengthRatio,
            CheckId.Ratio.Truncation,
            CheckId.Ratio.Repetition,
            CheckId.Ratio.Compression,
            CheckId.Ratio.Insertion,
        }
        .Select(id => Evaluate(id, bands, heldOut, settings))
        .ToList();

        return new RatioProfile(
            1,
            settings.PrecisionFloor,
            settings.BandQuantile,
            settings.HeldOutEvery,
            NegativeRecipe,
            sources,
            bands,
            checks);
    }

    public static IReadOnlyList<(string CheckId, string Target)> Negatives(string checkId, ParallelSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var target = RatioMeasures.Collapse(sample.Target);
        var words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (checkId)
        {
            case CheckId.Ratio.LengthRatio:
                return
                [
                    (checkId, target + " " + target),
                    (checkId, target[..Math.Max(1, target.Length / 3)]),
                ];

            case CheckId.Ratio.Truncation:
            {
                if (!RatioMeasures.EndsWithTerminal(sample.Source) || words.Length < 2)
                {
                    return [];
                }

                var keep = Math.Max(1, (int)Math.Floor(words.Length * 0.6));
                var cut = string.Join(' ', words.Take(keep)).TrimEnd('.', '!', '?', ':', ';', '…');

                return [(checkId, cut)];
            }

            case CheckId.Ratio.Repetition:
            {
                if (words.Length < 2)
                {
                    return [];
                }

                var tail = string.Join(' ', words.TakeLast(2));
                return [(checkId, target + string.Concat(Enumerable.Repeat(" " + tail, 6)))];
            }

            case CheckId.Ratio.Compression:
            {
                if (words.Length < 3)
                {
                    return [];
                }

                var phrase = string.Join(' ', words.Take(3));
                return [(checkId, target + string.Concat(Enumerable.Repeat(" " + phrase, 8)))];
            }

            case CheckId.Ratio.Insertion:
                return
                [
                    (checkId, "Here is the translation: " + target),
                    (checkId, target + " Note that this is a translation. The instructions were followed. As requested, nothing else was changed."),
                ];

            default:
                return [];
        }
    }

    public static RatioHit? Hit(string checkId, RatioBandSet band, string source, string target) => checkId switch
    {
        CheckId.Ratio.LengthRatio => RatioSignals.LengthRatio(band, source, target),
        CheckId.Ratio.Truncation => RatioSignals.Truncation(band, source, target),
        CheckId.Ratio.Repetition => RatioSignals.Repetition(band, target),
        CheckId.Ratio.Compression => RatioSignals.Compression(band, target),
        CheckId.Ratio.Insertion => RatioSignals.Insertion(band, source, target),
        _ => null,
    };

    private static RatioBandSet Band(string pair, string unit, List<ParallelSample> samples, double quantile)
    {
        var ratios = samples.Select(s => RatioMeasures.LengthRatio(s.Source, s.Target)).Where(r => r > 0).OrderBy(r => r).ToList();
        var runs = samples.Select(s => (double)RatioMeasures.RepetitionRun(s.Target)).ToList();
        var compressions = samples.Select(s => RatioMeasures.CompressionRatio(s.Target)).ToList();
        var excess = samples.Select(s => (double)(RatioMeasures.SentenceCount(s.Target) - RatioMeasures.SentenceCount(s.Source))).ToList();

        return new RatioBandSet(
            pair,
            unit,
            samples.Count,
            new RatioBand(Round(Quantile(ratios, quantile)), Round(Quantile(ratios, 1 - quantile)), Round(Quantile(ratios, 0.5)), ratios.Count),
            Limit(runs),
            Limit(compressions),
            Limit(excess));
    }

    private static RatioLimit Limit(List<double> values)
    {
        if (values.Count == 0)
        {
            return new RatioLimit(0, 0, 0);
        }

        var max = values.Max();
        return new RatioLimit(Round(max), Round(max), values.Count);
    }

    private static RatioCheckCalibration Evaluate(
        string checkId,
        IReadOnlyList<RatioBandSet> bands,
        List<ParallelSample> heldOut,
        CalibrationSettings settings)
    {
        var truePositives = 0;
        var falsePositives = 0;
        var negatives = 0;
        var positives = 0;

        foreach (var sample in heldOut)
        {
            var band = bands.FirstOrDefault(b => b.Pair == sample.Pair && b.Unit == sample.Unit);

            if (band is null)
            {
                continue;
            }

            positives++;

            if (Hit(checkId, band, sample.Source, sample.Target) is not null)
            {
                falsePositives++;
            }

            foreach (var (_, target) in Negatives(checkId, sample))
            {
                negatives++;

                if (Hit(checkId, band, sample.Source, target) is not null)
                {
                    truePositives++;
                }
            }
        }

        var precision = truePositives + falsePositives == 0 ? 0 : (double)truePositives / (truePositives + falsePositives);
        var recall = negatives == 0 ? 0 : (double)truePositives / negatives;
        var enabled = negatives > 0 && precision >= settings.PrecisionFloor;

        return new RatioCheckCalibration(
            checkId,
            enabled,
            Round(precision),
            Round(recall),
            positives,
            negatives,
            truePositives,
            falsePositives,
            settings.ScoreWeight);
    }

    private static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var position = q * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        var weight = position - lower;

        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static bool IsHeader(string cell) =>
        cell.Equals("English", StringComparison.OrdinalIgnoreCase)
        || cell.Equals("Term", StringComparison.OrdinalIgnoreCase)
        || cell.Equals("Czech word", StringComparison.OrdinalIgnoreCase);

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;

        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }
}
