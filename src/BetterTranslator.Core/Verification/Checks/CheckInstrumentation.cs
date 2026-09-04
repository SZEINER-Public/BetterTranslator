using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BetterTranslator.Core.Verification.Checks;

public static class CheckInstrumentation
{
    public const string Symbol = "BT_INSTRUMENT";

    public const string ProbeKey = "instrumentation/armed";

    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, string> FirstHits = new(StringComparer.Ordinal);

    public static bool Armed
    {
        get
        {
            Hit(ProbeKey);
            return Counts.ContainsKey(ProbeKey);
        }
    }

    [Conditional(Symbol)]
    public static void Hit(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        Counts.AddOrUpdate(key, 1, (_, count) => count + 1);
        FirstHits.GetOrAdd(key, _ => FirstStack());
    }

    public static IReadOnlyDictionary<string, int> Snapshot() =>
        new SortedDictionary<string, int>(Counts.Where(p => p.Key != ProbeKey).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal);

    public static string FirstHit(string key) => FirstHits.TryGetValue(key, out var stack) ? stack : string.Empty;

    public static void Reset()
    {
        Counts.Clear();
        FirstHits.Clear();
    }

    public static string Render()
    {
        var builder = new StringBuilder();

        foreach (var (key, count) in Snapshot())
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{key} {count}");
            builder.AppendLine("  first hit: " + FirstHit(key));
        }

        return builder.ToString();
    }

    private static string FirstStack()
    {
        var frames = new StackTrace(2, false).GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is not null && m.DeclaringType.Namespace is { } ns && ns.StartsWith("BetterTranslator", StringComparison.Ordinal))
            .Select(m => m!.DeclaringType!.Name + "." + m.Name)
            .Take(6);

        return string.Join(" <- ", frames);
    }
}
