using System.Globalization;

namespace BetterTranslator.Core.Services;

/// <summary>
/// The one formatter behind every timestamp the application shows. A stamp is
/// a relative part and an absolute part joined by a spaced hyphen, and the
/// relative part is dropped once something is older than a day, which is what
/// the design captures show.
/// </summary>
public static class RelativeTime
{
    private const string Separator = " - ";

    /// <summary>
    /// Sidebar row form: "42 min ago - Jul 28, 20:30".
    /// </summary>
    public static string Row(DateTimeOffset moment, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var absolute = moment.ToString("MMM d, HH:mm", culture);
        return Compose(Relative(moment, now), absolute);
    }

    /// <summary>
    /// Chat header form: "42 min ago - 28 July 2026, 20:30".
    /// </summary>
    public static string Header(DateTimeOffset moment, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var absolute = moment.ToString("d MMMM yyyy, HH:mm", culture);
        return Compose(Relative(moment, now), absolute);
    }

    /// <summary>
    /// Tooltip form: "Tuesday, 28 July 2026 at 20:29". Always absolute.
    /// </summary>
    public static string Tooltip(DateTimeOffset moment, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return moment.ToString("dddd, d MMMM yyyy", culture) + " at " + moment.ToString("HH:mm", culture);
    }

    /// <summary>
    /// The relative part on its own, or null once the moment is a day or more
    /// old, at which point the stamp carries the date only.
    /// </summary>
    public static string? Relative(DateTimeOffset moment, DateTimeOffset now)
    {
        var elapsed = now - moment;

        // A clock skew or a moment in the future reads as the present rather
        // than as a negative age.
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return minutes + " min ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "1 hour ago" : hours + " hours ago";
        }

        return null;
    }

    private static string Compose(string? relative, string absolute) =>
        relative is null ? absolute : relative + Separator + absolute;
}
