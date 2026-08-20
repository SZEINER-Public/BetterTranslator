using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.Updates.Notifications;

public sealed record PendingUpdateNotice
{
    public const int MaxIdentityLength = 96;

    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("commit")]
    public string Commit { get; init; } = string.Empty;

    [JsonPropertyName("installedVersion")]
    public string InstalledVersion { get; init; } = string.Empty;

    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset PublishedUtc { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("pageUrl")]
    public string PageUrl { get; init; } = string.Empty;

    [JsonPropertyName("stagedUtc")]
    public DateTimeOffset StagedUtc { get; init; }

    public string Identity => ReleaseIdentity.Of(Version, Commit);

    public bool IsUsable => Version.Length > 0 && ReleaseIdentity.IsWellFormed(Identity);

    public PendingUpdateNotice Sanitized() => this with
    {
        Tag = Clip(Tag, 64),
        Version = Clip(Version, 32),
        Commit = Clip(Commit, 40),
        InstalledVersion = Clip(InstalledVersion, 32),
        Notes = Clip(Notes, ReleaseInfo.MaxNotesLength),
        PageUrl = GitHubReleaseClient.IsReleasePage(PageUrl) ? PageUrl : string.Empty,
    };

    private static string Clip(string value, int length)
    {
        var trimmed = value.Trim();

        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }
}

public static class ReleaseIdentity
{
    public static string Of(string version, string commit)
    {
        var shortened = commit.Length > 7 ? commit[..7] : commit;

        return shortened.Length == 0
            ? version.Trim().ToLowerInvariant()
            : (version.Trim() + "+" + shortened).ToLowerInvariant();
    }

    public static bool IsWellFormed(string identity) =>
        identity.Length is > 0 and <= PendingUpdateNotice.MaxIdentityLength
        && identity.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    public static bool Same(string left, string right) =>
        IsWellFormed(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public sealed class PendingNoticeStore(UpdatePaths paths)
{
    private const int MaxBytes = 8 * 1024;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string File => Path.Combine(paths.UpdatesFolder, "pending.json");

    public PendingUpdateNotice? Read()
    {
        try
        {
            var file = new FileInfo(File);

            if (!file.Exists || file.Length > MaxBytes)
            {
                return null;
            }

            var notice = JsonSerializer
                .Deserialize<PendingUpdateNotice>(System.IO.File.ReadAllText(File, Encoding.UTF8), Format)
                ?.Sanitized();

            return notice is not null && notice.IsUsable ? notice : null;
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

    public bool Write(PendingUpdateNotice notice)
    {
        var clean = notice.Sanitized();

        if (!clean.IsUsable)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(paths.UpdatesFolder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(clean, Format), Encoding.UTF8);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                System.IO.File.Delete(File);
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
