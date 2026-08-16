using System.Text.Json;

namespace BetterTranslator.Runtime.Inference;

/// <summary>One instruction to the inference host.</summary>
public sealed record HostRequest
{
    public string Op { get; init; } = string.Empty;

    /// <summary>
    /// Which generation this is, and which one a stop is for.
    ///
    /// A stop has to name its target. The child reads ahead of the work on
    /// purpose, so a stop can arrive before the generation it names has taken
    /// its token -- it has to be remembered, and a remembered stop that named
    /// nothing would be applied to whatever ran next. Zero on everything that
    /// cannot be stopped, and zero never matches.
    /// </summary>
    public int Id { get; init; }

    public string? ModelPath { get; init; }

    public int ContextTokens { get; init; }

    public int GpuLayers { get; init; }

    /// <summary>The flavour the parent resolved, so the child cannot pick a different one.</summary>
    public string? Flavor { get; init; }

    /// <summary>
    /// Where the native library lives. Sent rather than recomputed: the parent
    /// knows the install folders, and a child that worked them out for itself
    /// could load a different build than the one the parent reported.
    /// </summary>
    public IReadOnlyList<string>? SearchPaths { get; init; }

    public string? Text { get; init; }

    public string? FromCode { get; init; }

    public string? FromName { get; init; }

    public string? ToCode { get; init; }

    public string? ToName { get; init; }

    public int MaxTokens { get; init; }

    public float Temperature { get; init; }

    public float TopP { get; init; }

    public int TopK { get; init; }

    public float RepeatPenalty { get; init; }

    public uint Seed { get; init; }
}

public sealed record HostResponse
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public string? Text { get; init; }

    public int Tokens { get; init; }

    /// <summary>
    /// The flavour the child actually loaded, answered on a load.
    ///
    /// Sent back rather than assumed, because the child is where the library is
    /// resolved and the parent cannot see the result: a flavour that is present
    /// but unloadable is substituted inside the child, and without this the
    /// parent would keep reporting the one it asked for.
    /// </summary>
    public string? Flavor { get; init; }

    /// <summary>Why that is not the flavour that was asked for, or null when it is.</summary>
    public string? FlavorNote { get; init; }

    /// <summary>
    /// The generation stopped because it was asked to, not because anything
    /// broke. It answers the request that was cancelled, so the pipe stays in
    /// step and the child keeps the model it has already loaded -- which is the
    /// whole reason a stop is asked for rather than the child being killed.
    /// </summary>
    public bool Cancelled { get; init; }

    public static HostResponse Failed(string error) => new() { Ok = false, Error = error };

    public static HostResponse Stopped() =>
        new() { Ok = false, Cancelled = true, Error = "the generation was stopped" };
}

/// <summary>
/// One JSON object per line, UTF-8, over a named pipe.
///
/// A line rather than a length prefix because the payloads are JSON strings and
/// JSON escapes its own newlines, so a line boundary cannot fall inside one.
/// Not stdout: the native runtime writes to the console itself -- "ggml_vulkan:
/// Found 1 Vulkan devices" would land in the middle of the protocol.
/// </summary>
public static class HostProtocol
{
    public const string ReadySignal = "ready";

    /// <summary>
    /// The one request that is answered by another request's response rather
    /// than by its own. It has to overtake a generation to be worth sending, so
    /// the child reads it off the same pipe while one is running and the parent
    /// goes on waiting for the single line the generation still owes it.
    /// </summary>
    public const string Cancel = "cancel";

    public const string Complete = "complete";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Encode<T>(T message) => JsonSerializer.Serialize(message, Json);

    public static T? Decode<T>(string line) => JsonSerializer.Deserialize<T>(line, Json);
}
