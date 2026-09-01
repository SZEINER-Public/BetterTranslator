using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Mac.Parity;

public sealed class DeltaEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("screens")]
    public string[] Screens { get; set; } = [];

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class DeltaFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("deltas")]
    public DeltaEntry[] Deltas { get; set; } = [];

    public static DeltaFile Load(string path) =>
        JsonSerializer.Deserialize<DeltaFile>(File.ReadAllText(path))
        ?? throw new InvalidDataException("deltas.json could not be read.");

    public IReadOnlyList<DeltaEntry> For(string screen) =>
        [.. Deltas.Where(d => d.Screens.Contains("*") || d.Screens.Contains(screen, StringComparer.Ordinal))];
}
