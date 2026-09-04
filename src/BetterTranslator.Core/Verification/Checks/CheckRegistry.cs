using System.Reflection;

namespace BetterTranslator.Core.Verification.Checks;

public sealed class CheckRegistry
{
    private static readonly Lazy<CheckRegistry> Discovered = new(() => Discover());

    private readonly SortedDictionary<string, ICheck> _checks = new(StringComparer.Ordinal);

    public static CheckRegistry Default => Discovered.Value;

    public IReadOnlyList<ICheck> Checks => [.. _checks.Values];

    public IReadOnlyList<ICheck> ForCategory(string category) =>
        [.. _checks.Values.Where(check => string.Equals(check.Category, category, StringComparison.Ordinal))];

    public ICheck? Find(string checkId) =>
        _checks.Values.FirstOrDefault(check => string.Equals(check.CheckId, checkId, StringComparison.Ordinal));

    public CheckRegistry Register(ICheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        _checks[Key(check)] = check;
        return this;
    }

    public static CheckRegistry Discover(params Assembly[] assemblies)
    {
        var registry = new CheckRegistry();
        var scanned = assemblies.Length == 0 ? [typeof(CheckRegistry).Assembly] : assemblies;

        foreach (var assembly in scanned)
        {
            foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || !typeof(ICheck).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                registry.Register((ICheck)Activator.CreateInstance(type)!);
            }
        }

        return registry;
    }

    public IReadOnlyList<CheckFinding> Run(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<CheckFinding>();

        foreach (var check in _checks.Values)
        {
            if (context.Settings.DisabledChecks.Contains(check.CheckId))
            {
                continue;
            }

            findings.AddRange(check.Run(context));
        }

        return findings;
    }

    private static string Key(ICheck check) => check.Category + "/" + check.CheckId;
}
