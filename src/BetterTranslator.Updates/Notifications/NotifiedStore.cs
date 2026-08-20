using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Updates.Notifications;

public sealed record NotifiedRelease
{
    [JsonPropertyName("identity")]
    public string Identity { get; init; } = string.Empty;

    [JsonPropertyName("shownUtc")]
    public DateTimeOffset ShownUtc { get; init; }

    [JsonPropertyName("dismissed")]
    public bool Dismissed { get; init; }
}

public sealed class NotifiedStore(string file)
{
    private const int MaxBytes = 4 * 1024;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static string DefaultFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterTranslator",
        "updates",
        "notified.json");

    public static NotifiedStore ForCurrentUser() => new(DefaultFile);

    public string File { get; } = file;

    public NotifiedRelease? Read()
    {
        try
        {
            var stored = new FileInfo(File);

            if (!stored.Exists || stored.Length > MaxBytes)
            {
                return null;
            }

            var recorded = JsonSerializer.Deserialize<NotifiedRelease>(
                System.IO.File.ReadAllText(File, Encoding.UTF8),
                Format);

            return recorded is not null && ReleaseIdentity.IsWellFormed(recorded.Identity) ? recorded : null;
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

    public bool WasShown(string identity) =>
        Read() is { } recorded && ReleaseIdentity.Same(recorded.Identity, identity);

    public void Record(string identity, bool dismissed)
    {
        if (!ReleaseIdentity.IsWellFormed(identity))
        {
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(File);

            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            System.IO.File.WriteAllText(
                File,
                JsonSerializer.Serialize(
                    new NotifiedRelease
                    {
                        Identity = identity,
                        ShownUtc = DateTimeOffset.UtcNow,
                        Dismissed = dismissed,
                    },
                    Format),
                Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
