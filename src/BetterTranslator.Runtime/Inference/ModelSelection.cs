namespace BetterTranslator.Runtime.Inference;

public sealed record ModelChoice(string Path, string? Reason)
{
    public static readonly ModelChoice Nothing = new(string.Empty, null);

    public bool HasModel => Path.Length > 0;
}

public static class ModelSelection
{
    public const string ChosenModelGone =
        "The model you chose is not installed any more, so the model for the effort in force is loaded instead.";

    public const string ChosenModelGoneWithNoTier =
        "The model you chose is not installed any more, so the first model found on this machine is loaded instead.";

    public static ModelChoice Resolve(
        ModelLibrary library,
        IReadOnlyList<LocalModel> found,
        string? chosenFileName,
        string? chosenPath,
        bool chosenExplicitly,
        string tierFileName)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(found);

        if (found.Count == 0)
        {
            return ModelChoice.Nothing;
        }

        var chosen = Locate(library, found, chosenFileName, chosenPath);

        if (chosenExplicitly && chosen is not null)
        {
            return new ModelChoice(chosen.Path, null);
        }

        var tier = library.Match(found, tierFileName ?? string.Empty);

        if (chosenExplicitly)
        {
            return tier is not null
                ? new ModelChoice(tier.Path, ChosenModelGone)
                : Fallback(library, found, ChosenModelGoneWithNoTier);
        }

        if (tier is not null)
        {
            return new ModelChoice(tier.Path, null);
        }

        return chosen is not null
            ? new ModelChoice(chosen.Path, null)
            : Fallback(library, found, null);
    }

    public static string FileNameOf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : System.IO.Path.GetFileName(path);

    private static LocalModel? Locate(
        ModelLibrary library,
        IReadOnlyList<LocalModel> found,
        string? fileName,
        string? path)
    {
        if (library.Match(found, fileName ?? string.Empty) is { } byName)
        {
            return byName;
        }

        return string.IsNullOrWhiteSpace(path)
            ? null
            : found.FirstOrDefault(model => string.Equals(model.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static ModelChoice Fallback(ModelLibrary library, IReadOnlyList<LocalModel> found, string? reason) =>
        library.Default(found) is { } model ? new ModelChoice(model.Path, reason) : new ModelChoice(string.Empty, reason);
}
