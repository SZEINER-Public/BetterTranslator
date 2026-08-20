using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Logging;

namespace BetterTranslator.Updates.Releases;

public sealed class GitHubReleaseClient : IReleaseClient
{
    public const string TokenVariable = "BETTERTRANSLATOR_GITHUB_TOKEN";

    public const string Owner = "SZEINER-Public";

    public const string Repository = "BetterTranslator";

    private const string ApiRoot = "https://api.github.com";
    private const string PagePrefix = "/" + Owner + "/" + Repository + "/releases";
    private const int MaxBodyBytes = 512 * 1024;
    private const int MaxReferenceBytes = 64 * 1024;
    private const int MaxTagLength = 64;
    private const int MaxAssetNameLength = 128;
    private const int MaxUrlLength = 512;
    private const int MaxAssets = 32;
    private const int MaxEntityTagLength = 128;
    private const long LatestAcceptedReset = 4102444800;

    private static readonly TimeSpan ShortestHoldOff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongestHoldOff = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions CacheFormat = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly UpdatePaths _paths;
    private readonly IUpdateLog _log;

    private DateTimeOffset _holdOffUntil = DateTimeOffset.MinValue;

    public GitHubReleaseClient(HttpClient http, UpdatePaths paths, IUpdateLog? log = null)
    {
        _http = http;
        _paths = paths;
        _log = log ?? NullUpdateLog.Instance;
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };

        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BetterTranslator", AgentVersion()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } supplied
            && IsToken(supplied.Trim()))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", supplied.Trim());
        }

        return http;
    }

    public static bool IsHttps(string? url) =>
        url is { Length: > 0 and <= MaxUrlLength }
        && Uri.TryCreate(url, UriKind.Absolute, out var address)
        && address.Scheme == Uri.UriSchemeHttps;

    public static bool IsReleasePage(string? url) =>
        IsHttps(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var address)
        && string.Equals(address.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && address.AbsolutePath.StartsWith(PagePrefix, StringComparison.OrdinalIgnoreCase);

    public static ReleaseInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            var tag = Text(root, "tag_name");

            if (tag.Length is 0 or > MaxTagLength)
            {
                return null;
            }

            var page = Text(root, "html_url");

            return new ReleaseInfo(tag, CommitOf(root), PublishedOf(root), AssetsOf(root))
            {
                Notes = NotesOf(root),
                PageUrl = IsReleasePage(page) ? page : string.Empty,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ReleaseLookup> LatestAsync(CancellationToken cancellationToken)
    {
        var cached = ReadCache();

        if (cached is not null && cached.HoldOffUntil > _holdOffUntil)
        {
            _holdOffUntil = cached.HoldOffUntil;
        }

        if (DateTimeOffset.UtcNow < _holdOffUntil)
        {
            return FromCache(cached)
                ?? ReleaseLookup.Failed(
                    $"The GitHub rate limit is spent, so the next check waits until {Moment(_holdOffUntil)}.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ApiRoot}/repos/{Owner}/{Repository}/releases/latest");

        if (cached is { ETag.Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        HttpResponseMessage response;

        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            _log.Write("The latest release could not be read", ex);

            return FromCache(cached) ?? ReleaseLookup.Failed($"GitHub could not be reached: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return FromCache(cached)
                    ?? ReleaseLookup.Failed("GitHub answered that nothing changed, but nothing was held here.");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                HoldOff(response, cached);

                var spent =
                    $"GitHub answered {(int)response.StatusCode} because its rate limit is spent. "
                    + $"The next check waits until {Moment(_holdOffUntil)}.";

                _log.Write(spent);

                return FromCache(cached) ?? ReleaseLookup.Failed(spent);
            }

            if (!response.IsSuccessStatusCode)
            {
                var refused = $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.";

                _log.Write(refused);

                return FromCache(cached) ?? ReleaseLookup.Failed(refused);
            }

            var body = await ReadBoundedAsync(response, MaxBodyBytes, cancellationToken).ConfigureAwait(false);

            if (body is null)
            {
                return FromCache(cached)
                    ?? ReleaseLookup.Failed("The answer from GitHub was larger than this reads.");
            }

            var release = Parse(body);

            if (release is null)
            {
                return FromCache(cached)
                    ?? ReleaseLookup.Failed("No published release could be read from what GitHub answered.");
            }

            if (!release.HasCommit)
            {
                var resolved = await CommitForTagAsync(release.Tag, cancellationToken).ConfigureAwait(false);

                if (resolved.Length > 0)
                {
                    release = release with { Commit = resolved };
                }
            }

            var exhausted = Exhausted(response) ? Reset(response, DateTimeOffset.UtcNow) : DateTimeOffset.MinValue;

            WriteCache(new ReleaseCache
            {
                ETag = EntityTag(response),
                Body = body,
                Commit = release.Commit,
                FetchedUtc = DateTimeOffset.UtcNow,
                HoldOffUntil = exhausted,
            });

            if (exhausted > _holdOffUntil)
            {
                _holdOffUntil = exhausted;
            }

            return ReleaseLookup.Found(release);
        }
    }

    private async Task<string> CommitForTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (!IsSafeTag(tag))
        {
            return string.Empty;
        }

        var reference = await ReadJsonAsync(
                $"{ApiRoot}/repos/{Owner}/{Repository}/git/ref/tags/{Uri.EscapeDataString(tag)}",
                cancellationToken)
            .ConfigureAwait(false);

        if (reference is null)
        {
            return string.Empty;
        }

        var target = TargetOf(reference);

        if (target.Sha.Length == 0)
        {
            return string.Empty;
        }

        if (!string.Equals(target.Kind, "tag", StringComparison.Ordinal))
        {
            return target.Sha;
        }

        var annotated = await ReadJsonAsync(
                $"{ApiRoot}/repos/{Owner}/{Repository}/git/tags/{target.Sha}",
                cancellationToken)
            .ConfigureAwait(false);

        return annotated is null ? string.Empty : TargetOf(annotated).Sha;
    }

    private async Task<string?> ReadJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await ReadBoundedAsync(response, MaxReferenceBytes, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            _log.Write("The tag behind the release could not be read", ex);

            return null;
        }
    }

    private static async Task<string?> ReadBoundedAsync(
        HttpResponseMessage response,
        int limit,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[8 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static ReleaseLookup? FromCache(ReleaseCache? cached)
    {
        if (cached is null)
        {
            return null;
        }

        var release = Parse(cached.Body);

        if (release is null)
        {
            return null;
        }

        if (!release.HasCommit && IsCommit(cached.Commit))
        {
            release = release with { Commit = cached.Commit };
        }

        return ReleaseLookup.Found(release);
    }

    private void HoldOff(HttpResponseMessage response, ReleaseCache? cached)
    {
        _holdOffUntil = Reset(response, DateTimeOffset.UtcNow);

        WriteCache((cached ?? new ReleaseCache()) with { HoldOffUntil = _holdOffUntil });
    }

    private static DateTimeOffset Reset(HttpResponseMessage response, DateTimeOffset now)
    {
        var until = now + ShortestHoldOff;

        if (Header(response, "x-ratelimit-reset") is { Length: > 0 } stamp
            && long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            && epoch is > 0 and < LatestAcceptedReset)
        {
            until = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        else if (Header(response, "Retry-After") is { Length: > 0 } after
                 && int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                 && seconds > 0)
        {
            until = now.AddSeconds(seconds);
        }

        if (until < now + ShortestHoldOff)
        {
            until = now + ShortestHoldOff;
        }

        return until > now + LongestHoldOff ? now + LongestHoldOff : until;
    }

    private static bool Exhausted(HttpResponseMessage response) =>
        Header(response, "x-ratelimit-remaining") is { Length: > 0 } remaining
        && int.TryParse(remaining, NumberStyles.None, CultureInfo.InvariantCulture, out var left)
        && left <= 0;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string EntityTag(HttpResponseMessage response)
    {
        var tag = Header(response, "ETag");

        if (tag is null || tag.Length is 0 or > MaxEntityTagLength)
        {
            return string.Empty;
        }

        foreach (var character in tag)
        {
            if (character is < ' ' or > '~')
            {
                return string.Empty;
            }
        }

        return tag;
    }

    private string CacheFile => Path.Combine(_paths.CacheFolder, "release-latest.json");

    private ReleaseCache? ReadCache()
    {
        try
        {
            var file = new FileInfo(CacheFile);

            if (!file.Exists || file.Length > 2 * MaxBodyBytes)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(CacheFile, Encoding.UTF8), CacheFormat);
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

    private void WriteCache(ReleaseCache cache)
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheFolder);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, CacheFormat), Encoding.UTF8);
        }
        catch (IOException ex)
        {
            _log.Write("The release cache could not be written", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Write("The release cache could not be written", ex);
        }
    }

    private static string CommitOf(JsonElement root)
    {
        var target = Text(root, "target_commitish");

        return IsCommit(target) ? target.ToLowerInvariant() : string.Empty;
    }

    private static DateTimeOffset PublishedOf(JsonElement root)
    {
        var stamp = Text(root, "published_at");

        if (stamp.Length == 0)
        {
            stamp = Text(root, "created_at");
        }

        return DateTimeOffset.TryParse(
            stamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var published)
            ? published
            : DateTimeOffset.UnixEpoch;
    }

    private static IReadOnlyList<ReleaseAsset> AssetsOf(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var found = new List<ReleaseAsset>();

        foreach (var element in assets.EnumerateArray())
        {
            if (found.Count >= MaxAssets)
            {
                break;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Text(element, "name");
            var url = Text(element, "browser_download_url");

            if (name.Length is 0 or > MaxAssetNameLength || !IsAssetName(name) || !IsHttps(url))
            {
                continue;
            }

            var state = Text(element, "state");

            if (state.Length > 0 && !string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found.Add(new ReleaseAsset(name, SizeOf(element), url, DigestOf(element)));
        }

        return found;
    }

    private static long SizeOf(JsonElement asset) =>
        asset.TryGetProperty("size", out var size)
        && size.ValueKind == JsonValueKind.Number
        && size.TryGetInt64(out var bytes)
        && bytes >= 0
            ? bytes
            : 0;

    private static string? DigestOf(JsonElement asset)
    {
        const string prefix = "sha256:";

        var digest = Text(asset, "digest");

        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..];

        return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static string NotesOf(JsonElement root)
    {
        var body = Text(root, "body");

        if (body.Length == 0)
        {
            return string.Empty;
        }

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            var clean = new string(trimmed.Where(character => !char.IsControl(character)).ToArray());

            return clean.Length > ReleaseInfo.MaxNotesLength ? clean[..ReleaseInfo.MaxNotesLength] : clean;
        }

        return string.Empty;
    }

    private static (string Sha, string Kind) TargetOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("object", out var target)
                || target.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, string.Empty);
            }

            var sha = Text(target, "sha");

            return IsCommit(sha) ? (sha.ToLowerInvariant(), Text(target, "type")) : (string.Empty, string.Empty);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static bool IsCommit(string value) =>
        value.Length is >= 7 and <= 40 && value.All(char.IsAsciiHexDigit);

    private static bool IsSafeTag(string tag) =>
        tag.Length is > 0 and <= MaxTagLength
        && tag.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsAssetName(string name) =>
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');

    private static bool IsToken(string token) =>
        token.Length is >= 8 and <= 255
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string AgentVersion()
    {
        var version = new string(BuildIdentity.Current.Version
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
            .ToArray());

        return version.Length > 0 ? version : "0.0.0";
    }

    private static string Moment(DateTimeOffset moment) => moment.ToString("u", CultureInfo.InvariantCulture);

    private sealed record ReleaseCache
    {
        [JsonPropertyName("etag")]
        public string ETag { get; init; } = string.Empty;

        [JsonPropertyName("body")]
        public string Body { get; init; } = string.Empty;

        [JsonPropertyName("commit")]
        public string Commit { get; init; } = string.Empty;

        [JsonPropertyName("fetchedUtc")]
        public DateTimeOffset FetchedUtc { get; init; }

        [JsonPropertyName("holdOffUntil")]
        public DateTimeOffset HoldOffUntil { get; init; }
    }
}
