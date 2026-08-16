using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterTranslator.Bench;

internal sealed record SliceRecord(string Name, int Keys, long Bytes, string Sha256, string Path);

internal sealed record CorpusManifest(
    string SourcePath,
    long SourceBytes,
    int SourceKeys,
    int SourceScalarValues,
    string SourceSha256,
    int EligibleKeys,
    string SelectionRule,
    IReadOnlyList<SliceRecord> Slices);

internal static class Corpus
{
    private const int MinimumValueLength = 20;
    private const int MaximumValueLength = 180;
    private const int MinimumProseWords = 4;
    private const int MinimumProseLetters = 20;

    internal const string SelectionRule =
        "keys are taken in file order; a key is eligible when its value is a string of 20 to 180 characters that "
        + "carries neither a pluralisation pipe nor an HTML tag nor a line break, and that still holds at least 4 "
        + "words and 20 ASCII letters once its placeholder tokens are removed, so that every selected value is real "
        + "prose a translator must change; each slice is a prefix of the next larger slice";

    internal static CorpusManifest Build(string sourcePath, IReadOnlyList<int> sizes)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var root = JsonNode.Parse(bytes)!.AsObject();

        var eligible = root
            .Where(pair => pair.Value is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            .Select(pair => (pair.Key, Value: pair.Value!.GetValue<string>()))
            .Where(pair => Eligible(pair.Value))
            .ToList();

        Directory.CreateDirectory(Paths.Corpus);

        var slices = new List<SliceRecord>();

        foreach (var size in sizes)
        {
            var take = Math.Min(size, eligible.Count);
            var slice = new JsonObject();

            foreach (var (key, value) in eligible.Take(take))
            {
                slice[key] = value;
            }

            var path = Path.Combine(Paths.Corpus, $"slice-{take}.json");
            var text = slice.ToJsonString(JsonOptions.Pretty) + "\n";

            Files.WriteText(path, text);

            slices.Add(new SliceRecord(
                Name: $"slice-{take}",
                Keys: take,
                Bytes: new FileInfo(path).Length,
                Sha256: Hashing.Sha256OfFile(path),
                Path: path));
        }

        return new CorpusManifest(
            SourcePath: sourcePath,
            SourceBytes: bytes.LongLength,
            SourceKeys: root.Count,
            SourceScalarValues: root.Count(pair => pair.Value is JsonValue value && value.GetValueKind() == JsonValueKind.String),
            SourceSha256: Hashing.Sha256OfBytes(bytes),
            EligibleKeys: eligible.Count,
            SelectionRule: SelectionRule,
            Slices: slices);
    }

    private static bool Eligible(string value)
    {
        if (value.Length is < MinimumValueLength or > MaximumValueLength)
        {
            return false;
        }

        if (!value.Contains(' ') || value.Contains('|') || value.Contains('<') || value.Contains('\n'))
        {
            return false;
        }

        var prose = value;

        foreach (var token in Placeholders.In(value))
        {
            prose = prose.Replace(token, " ", StringComparison.Ordinal);
        }

        var words = prose.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => word.Any(char.IsAsciiLetter))
            .ToList();

        return words.Count >= MinimumProseWords
            && prose.Count(char.IsAsciiLetter) >= MinimumProseLetters
            && prose.Any(char.IsAsciiLetterLower);
    }
}
