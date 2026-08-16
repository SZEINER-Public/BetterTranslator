using System.Globalization;

namespace BetterTranslator.Core.Services;

/// <summary>
/// The one formatter behind every size figure the application shows.
/// Attachments, the download manager, the cache table and every confirmation
/// go through here, so a model's size reads identically in all of them.
/// </summary>
public static class ByteSize
{
    private const double Step = 1024;

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// One decimal below ten, none at or above it: "4.2 KB", "42 MB",
    /// "184 MB", "2.4 GB". Bytes are always whole.
    /// </summary>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;

        if (bytes < 0)
        {
            return "0 B";
        }

        var (value, unit) = Scale(bytes);

        // A whole count of bytes never gains a decimal point.
        var digits = unit == 0 || value >= 10 ? 0 : 1;

        return value.ToString("N" + digits, culture) + " " + Units[unit];
    }

    /// <summary>
    /// Transfer rate, on the same unit ladder but always to one decimal:
    /// "28.3 MB/s", "42.6 MB/s". A rate moves while you read it, so the extra
    /// digit is what makes it look live rather than stuck.
    /// </summary>
    public static string FormatRate(double bytesPerSecond, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;

        if (bytesPerSecond <= 0)
        {
            return "0 B/s";
        }

        var (value, unit) = Scale(bytesPerSecond);
        var digits = unit == 0 ? 0 : 1;

        return value.ToString("N" + digits, culture) + " " + Units[unit] + "/s";
    }

    /// <summary>Walks the ladder once, shared by both formatters.</summary>
    private static (double Value, int Unit) Scale(double bytes)
    {
        var value = bytes;
        var unit = 0;

        while (value >= Step && unit < Units.Length - 1)
        {
            value /= Step;
            unit++;
        }

        return (value, unit);
    }
}
