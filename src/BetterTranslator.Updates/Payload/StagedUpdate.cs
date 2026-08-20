using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Updates.Payload;

public sealed record StagedUpdate
{
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("commit")]
    public string Commit { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("stagedUtc")]
    public DateTimeOffset StagedUtc { get; init; }

    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;
}

public sealed class StagedUpdateStore(string readyFile)
{
    private const int MaxBytes = 8 * 1024;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ReadyFile { get; } = readyFile;

    public StagedUpdate? Read()
    {
        try
        {
            var file = new FileInfo(ReadyFile);

            if (!file.Exists || file.Length > MaxBytes)
            {
                return null;
            }

            var staged = JsonSerializer.Deserialize<StagedUpdate>(
                System.IO.File.ReadAllText(ReadyFile, Encoding.UTF8),
                Format);

            if (staged is null || staged.File.Length == 0 || !System.IO.File.Exists(staged.File))
            {
                return null;
            }

            return staged;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(StagedUpdate staged)
    {
        var folder = Path.GetDirectoryName(ReadyFile);

        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        System.IO.File.WriteAllText(ReadyFile, JsonSerializer.Serialize(staged, Format), Encoding.UTF8);
    }

    public void Clear()
    {
        try
        {
            if (System.IO.File.Exists(ReadyFile))
            {
                System.IO.File.Delete(ReadyFile);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
