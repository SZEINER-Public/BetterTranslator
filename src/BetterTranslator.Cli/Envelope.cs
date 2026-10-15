using System.Text.Json;
using System.Text.Json.Serialization;
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Speech;

namespace BetterTranslator.Cli;

public sealed record ResultRow
{
    [JsonPropertyName("file")] public required string File { get; init; }

    [JsonPropertyName("status")] public required string Status { get; init; }

    [JsonPropertyName("out")] public string? Out { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("verification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Verification { get; init; }
}

public sealed record FailureDetail
{
    [JsonPropertyName("code")] public required int Code { get; init; }

    [JsonPropertyName("message")] public required string Message { get; init; }
}

public static class Envelope
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Transcription(Transcription transcription)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["file"] = transcription.Path,
            ["language"] = transcription.Language,
            ["mode"] = transcription.Mode == DictationMode.Code ? "code" : "prose",
            ["result"] = transcription.Text,
            ["segments"] = transcription.Segments.Select(s => new Dictionary<string, object?>
            {
                ["segment"] = s.Segment,
                ["start_ms"] = (long)s.Start.TotalMilliseconds,
                ["end_ms"] = (long)s.End.TotalMilliseconds,
                ["text"] = s.Text,
                ["raw"] = s.Raw,
                ["min_confidence"] = s.MinConfidence,
                ["substitutions"] = s.Substitutions.Select(x => new Dictionary<string, object?>
                {
                    ["kind"] = x.Kind.ToString().ToLowerInvariant(),
                    ["applied"] = x.Applied,
                    ["confidence"] = x.Confidence,
                    ["text_start"] = x.TextStart,
                    ["text_length"] = x.TextLength,
                    ["replacement"] = x.Replacement,
                }).ToArray(),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(envelope, Options);
    }

    public static string Text(TextTranslation translation, bool verify = false)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["from"] = translation.From,
            ["to"] = translation.To,
            ["model"] = translation.Model,
            ["result"] = translation.Text,
            ["note"] = translation.Note,
        };

        if (verify)
        {
            envelope["verification"] = translation.Verification.Envelope();
        }

        return JsonSerializer.Serialize(envelope, Options);
    }

    public static string Files(IReadOnlyList<FileTranslation> results, bool verify = false)
    {
        var failed = results.FirstOrDefault(r => r.Status != "ok");

        var envelope = new Dictionary<string, object?>
        {
            ["ok"] = failed is null,
            ["results"] = results.Select(r => new ResultRow
            {
                File = r.File,
                Status = r.Status,
                Out = r.Out,
                Error = r.Error,
                Verification = verify ? r.Verification.Envelope() : null,
            }).ToList(),
        };

        if (failed is not null)
        {
            envelope["error"] = new FailureDetail
            {
                Code = ExitCode.For(failed.ErrorCode),
                Message = results.Count == 1
                    ? failed.Error ?? "The file was not translated."
                    : $"{results.Count(r => r.Status != "ok")} of {results.Count} files were not translated. "
                        + $"The first failure was {failed.File}: {failed.Error}",
            };
        }

        return JsonSerializer.Serialize(envelope, Options);
    }

    public static string Languages(IReadOnlyList<LanguageRow> rows) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["languages"] = rows,
        },
        Options);

    public static string Models(IReadOnlyList<ModelRow> rows) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["models"] = rows,
        },
        Options);

    public static string Failure(int code, string message) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = new FailureDetail { Code = code, Message = message },
        },
        Options);
}

public sealed record LanguageRow
{
    [JsonPropertyName("code")] public required string Code { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("endonym")] public required string Endonym { get; init; }

    [JsonPropertyName("availability")] public required string Availability { get; init; }
}

public sealed record ModelRow
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("file")] public required string File { get; init; }

    [JsonPropertyName("path")] public required string Path { get; init; }

    [JsonPropertyName("installed")] public required bool Installed { get; init; }

    [JsonPropertyName("selected")] public required bool Selected { get; init; }
}
