namespace BetterTranslator.Core.Models;

public sealed record EffortTier(TranslationEffort Effort, string Label, string ModelId, string ModelName);

public sealed record EffortSelection(IReadOnlyList<EffortTier> Available, EffortTier? Selected, string? Notice)
{
    public bool NothingInstalled => Available.Count == 0;
}

public static class EffortTiers
{
    private const string LegacySimpleName = "Fast";

    public static EffortTier Simple { get; } =
        new(TranslationEffort.Simple, "Simple", "eurollm", "EuroLLM");

    public static EffortTier Thinking { get; } =
        new(TranslationEffort.Thinking, "Thinking", "translategemma", "TranslateGemma");

    public static IReadOnlyList<EffortTier> All { get; } = [Simple, Thinking];

    public static TranslationEffort Default => TranslationEffort.Thinking;

    public static string NothingInstalledLabel => "No model";

    public static string NothingInstalledNotice =>
        $"No translation model is installed. Install {Simple.ModelName} or {Thinking.ModelName} to translate.";

    public static EffortTier For(TranslationEffort effort) =>
        All.FirstOrDefault(tier => tier.Effort == effort) ?? Thinking;

    public static EffortTier? ForModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId)
            ? null
            : All.FirstOrDefault(tier => string.Equals(tier.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    public static TranslationEffort? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (string.Equals(stored, LegacySimpleName, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationEffort.Simple;
        }

        return Enum.TryParse<TranslationEffort>(stored, ignoreCase: true, out var parsed)
            && All.Any(tier => tier.Effort == parsed)
                ? parsed
                : null;
    }

    public static EffortSelection Resolve(IEnumerable<string>? installedModelIds, TranslationEffort? persisted)
    {
        var installed = new HashSet<string>(installedModelIds ?? [], StringComparer.OrdinalIgnoreCase);
        var available = All.Where(tier => installed.Contains(tier.ModelId)).ToArray();

        if (available.Length == 0)
        {
            return new EffortSelection(available, null, NothingInstalledNotice);
        }

        var kept = persisted is null
            ? null
            : Array.Find(available, tier => tier.Effort == persisted.Value);

        if (kept is not null)
        {
            return new EffortSelection(available, kept, null);
        }

        var chosen = Array.Find(available, tier => tier.Effort == Default) ?? available[0];

        return new EffortSelection(
            available,
            chosen,
            persisted is null ? null : Moved(persisted.Value, chosen));
    }

    private static string Moved(TranslationEffort persisted, EffortTier chosen)
    {
        var previous = For(persisted);

        return $"{previous.Label} needs {previous.ModelName}, which is not installed. {chosen.Label} is in use instead.";
    }
}
