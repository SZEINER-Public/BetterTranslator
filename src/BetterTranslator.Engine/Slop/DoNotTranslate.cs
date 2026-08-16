using System.Text.Json;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Slop;

/// <summary>
/// The two do-not-translate lists, and where they came from.
/// </summary>
public sealed record DoNotTranslateLists(
    IReadOnlyList<string> Strict,
    IReadOnlyList<string> Declinable,
    IReadOnlyList<string> Sources);

/// <summary>
/// Terms the translator must not turn into the target language. Ported from
/// `Import-DoNotTranslate` in `rag.ps1`.
///
/// The defect it exists for is recorded in the original at length: seven scripts
/// each carried their own literal copy of this list, compiled into an engine
/// whose own rules say it contains no host-specific value anywhere. The copies
/// had already drifted -- one knew nothing about Argon2, another nothing about
/// GGUF -- so a term added for one file type silently did not apply to another.
/// The comment above one of those arrays read "which list a term belongs on is
/// the config decision the operator makes", which was true of the design and
/// false of the code.
///
/// Two lists, because the distinction is real and only the operator can make it:
///
///   strict      hidden from the model entirely, like a code span, so it comes
///               back byte-identical. For names that must never inflect: a
///               product, a company, a file format, an acronym.
///   declinable  sent to the model and checked with a relaxed gate that accepts
///               a case ending. For a term that has become an ordinary noun in
///               the target language -- "a future llmster release" becoming
///               "budouci vydani LLMsteru" is right, and refusing it left the
///               whole sentence in English.
/// </summary>
public static class DoNotTranslate
{
    /// <summary>
    /// The shipped lists, plus a project's own where one is supplied.
    ///
    /// A project's terms are ADDED to the defaults rather than replacing them,
    /// so a project never has to restate ".NET" in order to add its own name.
    /// `replaceDefaults` in the project file opts out for a project that
    /// genuinely wants to start from nothing.
    /// </summary>
    public static DoNotTranslateLists Load(string? projectJson = null)
    {
        var strict = new List<string>();
        var declinable = new List<string>();
        var sources = new List<string>();

        var replaceDefaults = false;
        JsonElement? project = null;

        if (!string.IsNullOrWhiteSpace(projectJson))
        {
            project = Parse(projectJson!);

            if (project is { } p
                && Property(p, "doNotTranslate") is { } pd
                && Property(pd, "replaceDefaults") is { ValueKind: JsonValueKind.True or JsonValueKind.False } flag)
            {
                replaceDefaults = flag.GetBoolean();
            }
        }

        if (!replaceDefaults
            && Parse(Config.ConfigStore.BytesFor("rag-defaults")) is { } defaults
            && Property(defaults, "doNotTranslate") is { } dd)
        {
            Add(strict, Strings(dd, "strict"));
            Add(declinable, Strings(dd, "declinable"));
            sources.Add("rag.defaults.json");
        }

        if (project is { } proj && Property(proj, "doNotTranslate") is { } pdt)
        {
            Add(strict, Strings(pdt, "strict"));
            Add(declinable, Strings(pdt, "declinable"));
            sources.Add("project");
        }

        // A term on both lists is contradictory -- hidden from the model AND sent
        // to it -- so strict wins and the declinable copy is dropped. Keeping
        // both would make the behaviour depend on which gate ran first.
        var clean = declinable
            .Where(d => !strict.Any(s => string.Equals(s, d, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new DoNotTranslateLists(strict, clean, sources);
    }

    /// <summary>
    /// Case-insensitive de-duplication: the gate matches that way too, so
    /// keeping both "API" and "api" would only widen the pattern twice.
    /// </summary>
    private static void Add(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var trimmed = value.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!target.Any(e => string.Equals(e, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(trimmed);
            }
        }
    }

    private static JsonElement? Parse(string text) => Parse(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// A file that will not parse is no file, matching `Read-RagJson`: it
    /// returns null on anything it cannot read rather than throwing, because a
    /// broken project config must not stop the shipped defaults loading.
    /// </summary>
    private static JsonElement? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0)
        {
            return null;
        }

        try
        {
            // JsonDocument.Parse has no ReadOnlySpan overload; the array is the
            // shortest honest bridge and these files are kilobytes.
            using var document = JsonDocument.Parse(
                BomSafeJson.StripBom(utf8).ToArray(),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)]
            : [];
}
