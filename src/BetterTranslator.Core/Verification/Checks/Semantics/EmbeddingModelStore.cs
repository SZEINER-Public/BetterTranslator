namespace BetterTranslator.Core.Verification.Checks.Semantics;

public sealed record EmbeddingModelFiles(string ModelPath, string TokenizerPath);

public static class EmbeddingModelStore
{
    public const string TokenizerFileName = "multilingual-e5-small.tokenizer.json";

    public const string FolderName = "models";

    public static IReadOnlyList<string> Folders(string? modelsFolder)
    {
        var folders = new List<string>();

        Add(modelsFolder);
        Add(Path.Combine(AppContext.BaseDirectory, FolderName));

        return folders;

        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var full = Path.GetFullPath(folder);

            if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(full);
            }
        }
    }

    public static EmbeddingModelFiles? Find(string? modelsFolder, EmbeddingModelDescription? model = null)
    {
        model ??= EmbeddingModelDescription.MultilingualE5Small;

        foreach (var folder in Folders(modelsFolder))
        {
            var modelPath = Path.Combine(folder, model.FileName);
            var tokenizerPath = Path.Combine(folder, TokenizerFileName);

            if (File.Exists(modelPath) && File.Exists(tokenizerPath))
            {
                return new EmbeddingModelFiles(modelPath, tokenizerPath);
            }
        }

        return null;
    }

    public static string MissingReason(string? modelsFolder, EmbeddingModelDescription? model = null)
    {
        model ??= EmbeddingModelDescription.MultilingualE5Small;

        return $"embedding model '{model.Identity}' ({model.FileName} and {TokenizerFileName}) is not installed in: {string.Join("; ", Folders(modelsFolder))}";
    }

    public static EmbeddingHost Host(string? modelsFolder, EmbeddingModelDescription? model = null)
    {
        model ??= EmbeddingModelDescription.MultilingualE5Small;
        var files = Find(modelsFolder, model);

        if (files is null)
        {
            return EmbeddingHost.Unavailable(model.Identity, MissingReason(modelsFolder, model));
        }

        return new EmbeddingHost(
            () => new OnnxEmbeddingBackend(files.ModelPath, model.Identity),
            () => UnigramTokenizer.Load(files.TokenizerPath),
            model.Identity,
            model.QueryPrefix);
    }
}
