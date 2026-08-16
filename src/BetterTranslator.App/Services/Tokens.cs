using System.Windows;

namespace BetterTranslator.App.Services;

/// <summary>
/// Typed access to the token dictionaries from code. View models read timings
/// and metrics through here rather than carrying their own copy of a number
/// that already exists in Metrics.xaml.
/// </summary>
public static class Tokens
{
    public static T Get<T>(string key) => (T)Application.Current.FindResource(key);

    public static double Number(string key) => Get<double>(key);

    /// <summary>A token declared in milliseconds, as a TimeSpan.</summary>
    public static TimeSpan Milliseconds(string key) => TimeSpan.FromMilliseconds(Number(key));

    /// <summary>
    /// The same, or null where the dictionaries are not there to be read.
    ///
    /// For timings that only shape what is on screen. A view model is exercised
    /// headless as well, with no application or with one carrying no theme, and
    /// a delay before a placeholder appears has nothing to delay there. Asking
    /// whether an application exists is not enough: a bare one answers yes and
    /// then throws on the first key.
    /// </summary>
    public static TimeSpan? TryMilliseconds(string key) =>
        Application.Current?.TryFindResource(key) is double milliseconds
            ? TimeSpan.FromMilliseconds(milliseconds)
            : null;
}
