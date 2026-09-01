using System.Text.Json.Serialization;

namespace BetterTranslator.Mac.Parity;

public sealed class NodeRecord
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("zIndex")]
    public int ZIndex { get; set; }

    [JsonPropertyName("bounds")]
    public double[] Bounds { get; set; } = [0, 0, 0, 0];

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("fontSize")]
    public double? FontSize { get; set; }

    [JsonPropertyName("foregroundToken")]
    public string? ForegroundToken { get; set; }

    [JsonPropertyName("backgroundToken")]
    public string? BackgroundToken { get; set; }

    [JsonPropertyName("children")]
    public List<NodeRecord> Children { get; set; } = [];
}

public sealed class TreeRecord
{
    [JsonPropertyName("screen")]
    public string Screen { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;

    [JsonPropertyName("head")]
    public string Head { get; set; } = string.Empty;

    [JsonPropertyName("root")]
    public NodeRecord Root { get; set; } = new();
}
