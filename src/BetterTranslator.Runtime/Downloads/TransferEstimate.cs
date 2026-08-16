using System.Globalization;

namespace BetterTranslator.Runtime.Downloads;

/// <summary>
/// Rate and remaining time for a transfer. The remaining time is computed from
/// what is actually left over the recent rate, not from an average over the
/// whole run, so it settles rather than drifting after a slow start.
/// </summary>
public sealed class TransferEstimate
{
    /// <summary>
    /// Rate is smoothed over this window. Short enough to react to a stall,
    /// long enough not to jitter on every buffer.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();

    private long _total;

    public void Reset(long totalBytes)
    {
        _samples.Clear();
        _total = totalBytes;
    }

    /// <summary>
    /// Records progress. <paramref name="elapsed"/> is time since the transfer
    /// started, passed in so the calculation stays testable without a clock.
    /// </summary>
    public void Report(TimeSpan elapsed, long bytesSoFar)
    {
        _samples.Enqueue((elapsed, bytesSoFar));

        while (_samples.Count > 2 && elapsed - _samples.Peek().At > Window)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Bytes per second over the recent window, or 0 while unknown.</summary>
    public double BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return 0;
            }

            var first = _samples.Peek();
            var last = _samples.Last();

            var seconds = (last.At - first.At).TotalSeconds;
            if (seconds <= 0)
            {
                return 0;
            }

            return Math.Max(0, (last.Bytes - first.Bytes) / seconds);
        }
    }

    public long BytesSoFar => _samples.Count == 0 ? 0 : _samples.Last().Bytes;

    public long BytesRemaining => Math.Max(0, _total - BytesSoFar);

    /// <summary>
    /// Time left for what is actually remaining. Null while the rate is not
    /// yet known or the total is unknown, so the footer can stay quiet rather
    /// than print a wild figure.
    /// </summary>
    public TimeSpan? Remaining
    {
        get
        {
            var rate = BytesPerSecond;
            if (rate <= 0 || _total <= 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(BytesRemaining / rate);
        }
    }

    public double Fraction => _total <= 0 ? 0 : Math.Clamp((double)BytesSoFar / _total, 0, 1);

    /// <summary>Whole percent, the figure shown beside a progress row.</summary>
    public int Percent => (int)Math.Floor(Fraction * 100);

    /// <summary>
    /// "52 s left", "3 min left", "1 h 20 min left", or an empty string while
    /// the rate is not yet known.
    /// </summary>
    public static string FormatRemaining(TimeSpan? remaining, CultureInfo? culture = null)
    {
        if (remaining is not { } left)
        {
            return string.Empty;
        }

        culture ??= CultureInfo.CurrentCulture;

        if (left.TotalSeconds < 1)
        {
            return "almost done";
        }

        if (left.TotalSeconds < 60)
        {
            return ((int)Math.Ceiling(left.TotalSeconds)).ToString(culture) + " s left";
        }

        if (left.TotalMinutes < 60)
        {
            return ((int)Math.Ceiling(left.TotalMinutes)).ToString(culture) + " min left";
        }

        var hours = (int)left.TotalHours;
        var minutes = left.Minutes;

        return minutes == 0
            ? hours.ToString(culture) + " h left"
            : hours.ToString(culture) + " h " + minutes.ToString(culture) + " min left";
    }
}
