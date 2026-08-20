using System.Text;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.Updates.Payload;

public sealed record FetchVerdict(bool Staged, string Detail, StagedUpdate? Update)
{
    public static FetchVerdict Refused(string detail) => new(false, detail, null);
}

public sealed class UpdateDownloader(HttpClient http, UpdatePaths paths, IUpdateLog? log = null)
{
    private const int BufferSize = 256 * 1024;
    private const long MaxPayloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxChecksumBytes = 256 * 1024;

    private readonly IUpdateLog _log = log ?? NullUpdateLog.Instance;

    public async Task<string?> ExpectedChecksumAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        var asset = release.Payload(UpdatePaths.PayloadAssetName);

        return asset is null
            ? null
            : asset.Sha256 ?? await PublishedChecksumAsync(release, asset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FetchVerdict> FetchAsync(
        ReleaseInfo release,
        UpdateComparison comparison,
        string? expected,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        var asset = release.Payload(UpdatePaths.PayloadAssetName);

        if (asset is null)
        {
            return FetchVerdict.Refused(
                $"Release {release.Tag} publishes no {UpdatePaths.PayloadAssetName} to install.");
        }

        if (!GitHubReleaseClient.IsHttps(asset.DownloadUrl))
        {
            return FetchVerdict.Refused("The release asset is not offered over HTTPS.");
        }

        if (asset.SizeBytes > MaxPayloadBytes)
        {
            return FetchVerdict.Refused($"The release asset is {asset.SizeBytes} bytes, which is past what this will fetch.");
        }

        if (expected is null)
        {
            return FetchVerdict.Refused(
                $"Release {release.Tag} carries neither an asset digest nor a checksums file, so {asset.Name} cannot be "
                + "verified. Release publication must include a SHA-256 for the asset before it can be installed.");
        }

        paths.EnsureCreated();
        Delete(paths.PartialPayload);

        try
        {
            await DownloadAsync(asset, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Delete(paths.PartialPayload);
            _log.Write($"The download of {asset.Name} failed", ex);

            return FetchVerdict.Refused($"The download did not finish: {ex.Message}");
        }

        var verdict = await PayloadVerifier
            .VerifyAsync(paths.PartialPayload, expected, asset.SizeBytes, cancellationToken)
            .ConfigureAwait(false);

        if (!verdict.Accepted)
        {
            Delete(paths.PartialPayload);
            _log.Write($"Refused the download of {release.Tag}: {verdict.Detail}");

            return FetchVerdict.Refused(verdict.Detail);
        }

        Delete(paths.StagedPayload);
        File.Move(paths.PartialPayload, paths.StagedPayload);

        var staged = new StagedUpdate
        {
            Tag = release.Tag,
            Version = comparison.LatestVersion,
            Commit = comparison.LatestCommit,
            Sha256 = verdict.Sha256 ?? expected,
            SizeBytes = new FileInfo(paths.StagedPayload).Length,
            StagedUtc = DateTimeOffset.UtcNow,
            File = paths.StagedPayload,
        };

        new StagedUpdateStore(paths.ReadyFile).Write(staged);
        _log.Write($"Staged {release.Tag} at {paths.StagedPayload}. {verdict.Detail}");

        return new FetchVerdict(true, $"{comparison.LatestVersion} is staged and verified.", staged);
    }

    private async Task DownloadAsync(
        ReleaseAsset asset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength ?? asset.SizeBytes;

        if (declared > MaxPayloadBytes)
        {
            throw new IOException($"The asset is {declared} bytes, which is past what this will fetch.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            paths.PartialPayload,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        var buffer = new byte[BufferSize];
        long written = 0;
        var reported = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            written += read;

            if (written > MaxPayloadBytes)
            {
                throw new IOException("The asset kept sending past the size this will fetch.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            // Reported a percent at a time. A hundred or so updates is enough to
            // show a bar moving, where one per buffer would be thousands of
            // dispatches to the interface for no more information.
            if (progress is not null && declared > 0)
            {
                var whole = (int)(written * 100 / declared);

                if (whole > reported)
                {
                    reported = whole;
                    progress.Report(Math.Clamp(whole / 100d, 0, 1));
                }
            }
        }
    }

    private async Task<string?> PublishedChecksumAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        var checksums = release.Checksums();

        if (checksums is null
            || !GitHubReleaseClient.IsHttps(checksums.DownloadUrl)
            || checksums.SizeBytes > MaxChecksumBytes)
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(
                    checksums.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxChecksumBytes)
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

                if (buffer.Length + read > MaxChecksumBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return PayloadVerifier.FindChecksum(
                Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length),
                asset.Name);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _log.Write("The published checksums could not be read", ex);

            return null;
        }
    }

    private static void Delete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
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
