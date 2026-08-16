using System.Text.Json.Serialization;

namespace BetterTranslator.Engine.Languages;

/// <summary>
/// One row of the language registry, as `config\languages.json` carries it.
///
/// What a given model can do with the language is not here: that is a set of
/// references into this registry, kept with its provenance in
/// `model-languages.json` and read through <see cref="ModelLanguages"/>. Adding
/// a model means adding a sourced set there and not editing a row here.
/// </summary>
public sealed record LanguageEntry
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The language's own name in its own script. Load-bearing, not decoration:
    /// it is what <see cref="ScriptPattern"/> derives a script check from, so
    /// the check follows the registry and adding a language adds its script too.
    /// </summary>
    [JsonPropertyName("native")]
    public string Native { get; init; } = string.Empty;

    [JsonPropertyName("script")]
    public string Script { get; init; } = string.Empty;

    [JsonPropertyName("dir")]
    public string Dir { get; init; } = "ltr";

    /// <summary>Alternate codes that resolve here. Absent on most rows.</summary>
    [JsonPropertyName("alias")]
    public IReadOnlyList<string>? Alias { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>The registry document. `_about` is prose for a human and is not read.</summary>
internal sealed record LanguageDocument
{
    [JsonPropertyName("languages")]
    public IReadOnlyList<LanguageEntry> Languages { get; init; } = [];
}
