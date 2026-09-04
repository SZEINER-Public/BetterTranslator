namespace BetterTranslator.Core.Verification.Checks.Ratio;

public sealed record RatioHit(string CheckId, double Value, double Threshold, int Confidence, string Detail);

public static class RatioSignals
{
    public static RatioHit? LengthRatio(RatioBandSet band, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(band);

        var ratio = RatioMeasures.LengthRatio(source, target);

        if (ratio <= 0 || band.LengthRatio.Samples == 0)
        {
            return null;
        }

        return OutsideBand(Checks.CheckId.Ratio.LengthRatio, band.LengthRatio, ratio, "length ratio");
    }

    public static RatioHit? Truncation(RatioBandSet band, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(band);

        if (!RatioMeasures.EndsWithTerminal(source) || RatioMeasures.EndsWithTerminal(target))
        {
            return null;
        }

        var ratio = RatioMeasures.LengthRatio(source, target);

        if (ratio <= 0 || band.LengthRatio.Samples == 0 || ratio >= band.LengthRatio.Low)
        {
            return null;
        }

        var confidence = BandConfidence(band.LengthRatio, ratio);

        return new RatioHit(
            Checks.CheckId.Ratio.Truncation,
            ratio,
            band.LengthRatio.Low,
            confidence,
            $"source ends with terminal punctuation, target does not, length ratio {Format(ratio)} below band floor {Format(band.LengthRatio.Low)}");
    }

    public static RatioHit? Repetition(RatioBandSet band, string target)
    {
        ArgumentNullException.ThrowIfNull(band);

        var run = RatioMeasures.RepetitionRun(target);

        return AboveLimit(Checks.CheckId.Ratio.Repetition, band.RepetitionRun, run, "repeated n-gram run");
    }

    public static RatioHit? Compression(RatioBandSet band, string target)
    {
        ArgumentNullException.ThrowIfNull(band);

        var ratio = RatioMeasures.CompressionRatio(target);

        return AboveLimit(Checks.CheckId.Ratio.Compression, band.CompressionRatio, ratio, "compression ratio");
    }

    public static RatioHit? Insertion(RatioBandSet band, string source, string target)
    {
        ArgumentNullException.ThrowIfNull(band);

        var pattern = RatioMeasures.InsertionMatch(source, target);

        if (pattern is not null)
        {
            return new RatioHit(
                Checks.CheckId.Ratio.Insertion,
                1,
                0,
                band.SentenceExcess.Samples == 0 ? 50 : 90,
                $"target carries model text '{pattern}'");
        }

        var excess = RatioMeasures.SentenceCount(target) - RatioMeasures.SentenceCount(source);

        return AboveLimit(Checks.CheckId.Ratio.Insertion, band.SentenceExcess, excess, "sentence count excess");
    }

    public static int BandConfidence(RatioBand band, double value)
    {
        var width = Math.Max(band.High - band.Low, 1e-9);
        var distance = value < band.Low ? band.Low - value : value > band.High ? value - band.High : 0;

        return (int)Math.Clamp(Math.Round(100 * distance / width, MidpointRounding.AwayFromZero), 0, 100);
    }

    public static int LimitConfidence(RatioLimit limit, double value)
    {
        var scale = Math.Max(Math.Abs(limit.Limit), 1);
        var distance = value - limit.Limit;

        return (int)Math.Clamp(Math.Round(100 * distance / scale, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static RatioHit? OutsideBand(string checkId, RatioBand band, double value, string label)
    {
        if (value >= band.Low && value <= band.High)
        {
            return null;
        }

        var confidence = BandConfidence(band, value);

        if (confidence == 0)
        {
            return null;
        }

        var edge = value < band.Low ? band.Low : band.High;

        return new RatioHit(
            checkId,
            value,
            edge,
            confidence,
            $"{label} {Format(value)} outside band {Format(band.Low)} to {Format(band.High)} ({band.Samples} samples)");
    }

    private static RatioHit? AboveLimit(string checkId, RatioLimit limit, double value, string label)
    {
        if (limit.Samples == 0 || value <= limit.Limit)
        {
            return null;
        }

        var confidence = LimitConfidence(limit, value);

        if (confidence == 0)
        {
            return null;
        }

        return new RatioHit(
            checkId,
            value,
            limit.Limit,
            confidence,
            $"{label} {Format(value)} above limit {Format(limit.Limit)} ({limit.Samples} samples)");
    }

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
