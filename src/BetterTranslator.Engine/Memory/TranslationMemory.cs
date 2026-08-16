using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Text;

namespace BetterTranslator.Engine.Memory;

/// <summary>One approved source-and-target pair.</summary>
public sealed record MemoryPair
{
    [JsonPropertyName("src")] public string Source { get; init; } = string.Empty;

    [JsonPropertyName("tgt")] public string Target { get; init; } = string.Empty;

    /// <summary>Locale key, or "L12" for a line-aligned document.</summary>
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;

    [JsonPropertyName("origin")] public string Origin { get; init; } = string.Empty;

    /// <summary>
    /// Which line of the store this came from. It travels with the entry because
    /// a hit has to be able to say WHICH entry answered.
    /// </summary>
    [JsonIgnore] public int Row { get; init; }
}

/// <summary>
/// Translation memory: the project's own already-approved translation.
///
/// The highest-value input in the system and the cheapest -- it costs no model
/// call to produce and none to use. An exact hit returns the approved target
/// verbatim: instant, free, and consistent by construction, which is the one
/// property a model cannot give you.
///
/// Ported from `rag-memory.ps1`. Storage is JSONL, one entry per line, so
/// appending is a write and never a rewrite and a corrupt line costs one entry
/// rather than the file. UTF-8 with no byte order mark, because a BOM makes the
/// first parse fail with a column-1 error that reads like malformed data.
/// </summary>
public sealed class TranslationMemory
{
    private readonly Dictionary<string, MemoryPair> _entries = new(StringComparer.Ordinal);

    public string Language { get; private init; } = "cs";

    public string? Path { get; private init; }

    public int Count => _entries.Count;

    /// <summary>Lines that would not parse. Reported rather than hidden.</summary>
    public int Malformed { get; private set; }

    public IReadOnlyCollection<MemoryPair> Entries => _entries.Values;

    /// <summary>
    /// Whitespace-normalised, because the same sentence wrapped differently is
    /// the same sentence, and a document routinely contains both.
    /// </summary>
    public static string KeyFor(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Whitespace.Replace(text, " ").Trim();

    /// <summary>
    /// Reads a store. A line that will not parse costs one entry, not the file.
    /// The first entry for a source wins: the file is append-ordered, so the
    /// oldest approved rendering is the established one.
    /// </summary>
    public static TranslationMemory Load(string? path, string language = "cs")
    {
        var memory = new TranslationMemory { Language = language, Path = path };

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return memory;
        }

        var row = 0;

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<MemoryPair>(BomSafeJson.StripBom(line), BomSafeJson.Options);
                var key = KeyFor(entry?.Source);

                if (entry is not null && key.Length > 0 && !memory._entries.ContainsKey(key))
                {
                    memory._entries[key] = entry with { Row = row };
                }
            }
            catch (JsonException)
            {
                memory.Malformed++;
            }

            row++;
        }

        return memory;
    }

    /// <summary>
    /// An exact hit, or null. This is the whole point: no model, no similarity,
    /// no threshold -- the approved target, verbatim.
    /// </summary>
    public MemoryPair? FindExact(string? source)
    {
        var key = KeyFor(source);

        return key.Length > 0 && _entries.TryGetValue(key, out var found) ? found : null;
    }

    /// <summary>
    /// Appends pairs whose source is not already present, and returns how many
    /// were added. Never rewrites, so an interrupted run leaves every previously
    /// written entry intact.
    /// </summary>
    public int Add(IEnumerable<MemoryPair> pairs)
    {
        var added = new List<string>();

        foreach (var pair in pairs)
        {
            if (pair.Source.Length == 0 || pair.Target.Length == 0)
            {
                continue;
            }

            var key = KeyFor(pair.Source);

            if (key.Length == 0 || _entries.ContainsKey(key))
            {
                continue;
            }

            _entries[key] = pair;
            added.Add(JsonSerializer.Serialize(pair, Compact));
        }

        if (added.Count > 0 && Path is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            // UTF8Encoding(false) has no preamble, so appending never writes a
            // BOM into the middle or the head of the file.
            using var writer = new StreamWriter(Path, append: true, new UTF8Encoding(false));

            foreach (var line in added)
            {
                writer.WriteLine(line);
            }
        }

        return added.Count;
    }

    /// <summary>
    /// Builds pairs from a document and its translation.
    ///
    /// The translator guarantees the line count is preserved, so line i of one
    /// corresponds to line i of the other. That makes every accepted line a free
    /// memory entry, and it is what lets the memory bootstrap itself on the very
    /// first document.
    /// </summary>
    /// <param name="validate">
    /// Admit a pair only if it would pass the translator's own gates. A memory
    /// entry is not just stored, it is PROMOTED -- fuzzy retrieval hands it to
    /// the model as an approved example to imitate -- so a bad entry does not sit
    /// harmlessly in a file, it teaches.
    /// </param>
    public static IReadOnlyList<MemoryPair> FromAligned(
        string sourceText,
        string targetText,
        string origin = "",
        int minLetters = 8,
        bool validate = true)
    {
        var src = sourceText.ReplaceLineEndings("\n").Split('\n');
        var tgt = targetText.ReplaceLineEndings("\n").Split('\n');

        if (src.Length != tgt.Length)
        {
            throw new ArgumentException(
                $"line counts differ ({src.Length} vs {tgt.Length}) - files are not aligned", nameof(targetText));
        }

        var pairs = new List<MemoryPair>();
        var inFence = false;

        for (var i = 0; i < src.Length; i++)
        {
            if (Fence.IsMatch(src[i]))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence
                || string.Equals(src[i], tgt[i], StringComparison.Ordinal)   // untranslated
                || src[i].Trim().Length == 0
                || tgt[i].Trim().Length == 0
                || Letter.Matches(src[i]).Count < minLetters)
            {
                continue;
            }

            if (validate
                && (ChunkIntegrity.Check(src[i], tgt[i], minLengthRatio: 0.35) is not null
                    || OutputLeak.Check(src[i], tgt[i], maxLengthRatio: 2.4) is not null))
            {
                continue;
            }

            if (MostlyUntranslated(src[i], tgt[i]))
            {
                continue;
            }

            pairs.Add(new MemoryPair
            {
                Source = src[i].Trim(),
                Target = tgt[i].Trim(),
                Key = $"L{i + 1}",
                Origin = origin,
            });
        }

        return pairs;
    }

    /// <summary>
    /// A mostly-English target, which no other gate can see. One Czech word with
    /// the English left standing preserves every code span, keeps its length and
    /// leaks nothing -- so it passes both gates above, and as an exemplar it
    /// teaches the model to do exactly that.
    ///
    /// Comparing word sets catches it without knowing either language: a real
    /// translation shares proper nouns and code with its source, not most of its
    /// vocabulary. Words inside code spans are excluded first, since those are
    /// supposed to be identical on both sides.
    /// </summary>
    private static bool MostlyUntranslated(string source, string target)
    {
        var sourceWords = Words(source);

        if (sourceWords.Count < 4)
        {
            return false;
        }

        var targetWords = Words(target);

        if (targetWords.Count == 0)
        {
            return false;
        }

        var set = new HashSet<string>(targetWords, StringComparer.Ordinal);
        var shared = sourceWords.Count(set.Contains);

        return (double)shared / sourceWords.Count > 0.6;
    }

    private static List<string> Words(string line) =>
        [.. Word.Matches(CodeSpan.Replace(line, " ")).Select(m => m.Value.ToLowerInvariant())];

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^\s*```", RegexOptions.Compiled);
    private static readonly Regex Letter = new(@"\p{L}", RegexOptions.Compiled);
    private static readonly Regex CodeSpan = new("`[^`]*`", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"\p{L}{3,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
