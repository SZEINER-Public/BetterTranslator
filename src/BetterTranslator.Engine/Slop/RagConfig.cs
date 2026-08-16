namespace BetterTranslator.Engine.Slop;

/// <summary>
/// Everything the guards and the prompt need for one target language. Ported
/// from `Import-RagConfig` in `rag.ps1`.
///
/// The shipped defaults are real values rather than placeholders, so running
/// with no project configuration exercises the same code path a configured
/// project would.
/// </summary>
public sealed record RagConfig
{
    public required string Language { get; init; }

    /// <summary>
    /// Whether a second pass is worth running. True for Czech in the shipped
    /// defaults, measured rather than assumed -- see the `_measured` note in
    /// `rag.defaults.json`.
    /// </summary>
    public bool SecondPass { get; init; }

    /// <summary>
    /// The per-language rules, injected into the system prompt on every request.
    /// Empty when no rules file exists, so the prompt is unchanged from before
    /// and the baseline never degrades.
    /// </summary>
    public string RulesText { get; init; } = string.Empty;

    public SlopConfig Slop { get; init; } = SlopConfig.Default();

    public GlossaryTables Glossary { get; init; } = GlossaryTables.Empty;

    /// <summary>
    /// The rules are a prompt prefix, not a document. An unbounded file would
    /// eat the chunk budget and truncate the answer.
    /// </summary>
    public const int MaxRulesLength = 4000;

    private static readonly Dictionary<string, RagConfig> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The configuration in force for a language.
    ///
    /// Cached per language: reading the rules file on every chunk would be
    /// wasteful and would let the file change mid-document, which is the same
    /// reason the reference caches it in `Get-RagFor`.
    /// </summary>
    public static RagConfig For(string language)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(language, out var cached))
            {
                return cached;
            }

            var config = Build(language);
            Cache[language] = config;
            return config;
        }
    }

    /// <summary>
    /// Drops the cache, so the next read picks up an edited file.
    ///
    /// Needed because the cache is what keeps a rules file from changing
    /// mid-document, and that same property means a glossary saved from Settings
    /// would not be seen until the app restarted.
    /// </summary>
    public static void Forget()
    {
        lock (Cache)
        {
            Cache.Clear();
        }
    }

    private static RagConfig Build(string language) => new()
    {
        Language = language,
        SecondPass = string.Equals(language, "cs", StringComparison.OrdinalIgnoreCase),
        RulesText = Rules(language),
        Slop = SlopConfig.Default(language),
        // Active, never Shipped. The shipped file is a worked example of ONE
        // project's vocabulary; loading it as a default asserted that project's
        // terminology over everybody's text.
        //
        // Fully qualified: the property below is also called Glossary and would
        // otherwise shadow the type.
        Glossary = BetterTranslator.Engine.Slop.Glossary.Active(language),
    };

    /// <summary>
    /// Hyphenated resource name, never "rules.cs.md": MSBuild reads the
    /// second-to-last extension as a culture, and a file named that way is built
    /// into a Czech satellite assembly and is absent from this one at runtime.
    /// </summary>
    private static string Rules(string language)
    {
        var resource = $"BetterTranslator.Engine.Data.rules-{language}.md";

        using var stream = typeof(RagConfig).Assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var text = reader.ReadToEnd().Trim();

        return text.Length > MaxRulesLength ? text[..MaxRulesLength] : text;
    }
}
