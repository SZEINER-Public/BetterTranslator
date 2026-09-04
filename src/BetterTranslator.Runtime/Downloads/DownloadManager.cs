using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

/// <summary>
/// Streams a component to disk, reporting a live rate and a remaining time
/// computed from what is actually left. Nothing here touches the UI thread;
/// progress arrives on an IProgress the caller marshals.
/// </summary>
public sealed class DownloadManager(HttpClient httpClient, InstallPaths paths, ModelResolver? resolver = null)
{
    /// <summary>
    /// Big enough that the rate reads steadily, small enough that a cancel
    /// lands promptly.
    ///
    /// Left at 128 KiB deliberately. The measured link saturates at ~14 MiB/s,
    /// which is about 110 reads a second at this size -- a rate at which the read
    /// loop is nowhere near the bottleneck, so a larger buffer would only make a
    /// cancel land later.
    /// </summary>
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// A stalled connection must fail rather than hold the session open. This is
    /// separate from the client-wide timeout on purpose, per fetch.ps1: one
    /// bound covers waiting for the first byte, this one covers a transfer that
    /// starts and then stops, which a single overall timeout cannot express
    /// without also capping how long a legitimate 14 GiB download may take.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads one component. The file is written to a .part beside its
    /// destination and moved into place only once complete, so a cancelled or
    /// failed run never leaves a half file looking installed.
    /// </summary>
    public async Task<DownloadProgress> DownloadAsync(
        ModelComponent component,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installed = await FetchOneAsync(component, null, progress, cancellationToken).ConfigureAwait(false);

        // The component's own artifact is not the whole component. A CUDA
        // runtime without its cuBLAS libraries beside it is a file that cannot
        // load, and stopping here is exactly what shipped as "CUDA is slow".
        if (installed.State != DownloadState.Installed || component.Companions.Count == 0)
        {
            return installed;
        }

        foreach (var companion in component.Companions)
        {
            if (ComponentInstallState.ArtifactMatches(paths.PathFor(companion), companion.SizeBytes))
            {
                continue;
            }

            if (companion.DownloadUrl is null)
            {
                return Report(progress, Failed(
                    component,
                    $"{component.Name} needs {companion.FileName} beside it and there is no link configured for it, "
                    + "so installing would leave a runtime that cannot load."));
            }

            var beside = await FetchOneAsync(
                AsComponent(component, companion),
                IntegrityOf(companion),
                progress,
                cancellationToken).ConfigureAwait(false);

            if (beside.State != DownloadState.Installed)
            {
                // Reported against the component the reader ticked, naming the
                // file that actually failed. A row that went quiet while a
                // dependency failed would look installed and not load.
                return Report(progress, beside.State == DownloadState.Cancelled
                    ? beside with { ComponentId = component.Id }
                    : Failed(component, $"{companion.FileName} could not be installed: {beside.Failure}"));
            }
        }

        return Report(progress, installed with { TotalBytes = component.InstallBytes });
    }

    /// <summary>
    /// A companion borrowed into the shape the transfer already understands, so
    /// resume, the stall bound, verify-then-promote and the local-copy search all
    /// apply to it unchanged rather than being written a second time.
    /// </summary>
    private static ModelComponent AsComponent(ModelComponent parent, CompanionArtifact companion) => new()
    {
        Id = parent.Id + "+" + Path.GetFileNameWithoutExtension(companion.FileName),
        Name = companion.FileName,
        Kind = parent.Kind,
        Summary = companion.Reason,
        SizeBytes = companion.SizeBytes,
        Version = parent.Version,
        ArtifactFileName = companion.FileName,
        DownloadUrl = companion.DownloadUrl,
    };

    /// <summary>
    /// Companions carry their own integrity rather than sitting in the manifest,
    /// because the manifest is keyed by Drive file id and these were measured
    /// from the toolkit they came from. Their hashes are real, which makes them
    /// the only artifacts here whose contents are checked rather than just their
    /// length.
    /// </summary>
    private static ArtifactIntegrity IntegrityOf(CompanionArtifact companion) => new()
    {
        FileId = companion.FileName,
        FileName = companion.FileName,
        SizeBytes = companion.SizeBytes,
        Sha256 = companion.Sha256,
    };

    private async Task<DownloadProgress> FetchOneAsync(
        ModelComponent component,
        ArtifactIntegrity? integrity,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (component.DownloadUrl is null)
        {
            return Failed(component, "No download link is configured for this component.");
        }

        var destination = paths.PathFor(component);
        var partial = destination + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Before any socket: the fastest download is the one that never happens.
        // Only a file already at the destination, though. This used to skip on
        // IsSatisfied, which is also true of a copy in another tool's store --
        // so a row the reader had deliberately ticked, and which the screen had
        // priced at 5.2 GB, was answered by doing nothing at all, and Install
        // fell straight through to the next screen having fetched nothing.
        // MissingDependencies counts as here too: it means this component's own
        // artifact matched and something beside it did not, so re-fetching the
        // artifact would move 138 MB to no purpose. The caller goes on to the
        // companions, which is what is actually absent.
        if (resolver?.Resolve(component) is
            { Reason: PresenceReason.InstalledHere or PresenceReason.MissingDependencies or PresenceReason.MissingSystemRuntime } here)
        {
            return Report(progress, Installed(component, here));
        }

        // Cross-process, because the store is machine-global: two app instances
        // must not write the same weights at once.
        using var storeLock = await ArtifactLock.AcquireAsync(component.FileName, cancellationToken)
            .ConfigureAwait(false);

        // Re-check under the lock. Whoever held it may have just finished the
        // very file this call is about to fetch.
        var found = resolver?.Resolve(component);

        if (found is { Reason: PresenceReason.InstalledHere } afterWait)
        {
            return Report(progress, Installed(component, afterWait));
        }

        // It exists on the machine, just not where this install writes. Copying
        // it beats fetching the same bytes over the network, and it is the same
        // request either way: the reader asked for the file to be in the chosen
        // folder. Verified and promoted exactly as a download is, so a corrupt
        // or truncated source cannot arrive looking installed.
        if (found is { Reason: PresenceReason.InstalledElsewhere, Path: { Length: > 0 } localCopy }
            && !string.Equals(localCopy, destination, StringComparison.OrdinalIgnoreCase)
            && File.Exists(localCopy))
        {
            return await CopyIntoPlaceAsync(component, integrity, localCopy, destination, partial, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var estimate = new TransferEstimate();
        var clock = Stopwatch.StartNew();

        // Resume from whatever a previous run left behind. The host answers 206
        // with the exact offset, measured, so this continues rather than restarts.
        var resumeFrom = ResumeOffset(partial, component.SizeBytes);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, component.DownloadUrl);

            if (resumeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            }

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // A server that ignores Range answers 200 with the whole file. Honour
            // that rather than appending a second copy onto the .part.
            if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                resumeFrom = 0;
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed(component, Explain(response.StatusCode, component.Name));
            }

            // Status alone never proves you got the file: Drive answers a quota
            // or virus-scan interstitial with 200 and an HTML body.
            if (response.Content.Headers.ContentType?.MediaType == "text/html")
            {
                return Failed(
                    component,
                    $"Google Drive returned a web page instead of {component.Name}. The download quota for this file is " +
                    "most likely exhausted; it resets after about a day.");
            }

            // Prefer the real length; fall back to the catalogue figure so the
            // bar still has a denominator.
            var total = (response.Content.Headers.ContentLength ?? component.SizeBytes) + resumeFrom;
            estimate.Reset(total);

            progress?.Report(new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Downloading,
                TotalBytes = total,
            });

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(
                partial,
                resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long written = resumeFrom;
                var lastReport = TimeSpan.Zero;

                // A resumed transfer cannot hash what it did not read, so the
                // running hash only stands for a download that started at zero.
                var hashable = resumeFrom == 0;

                while (true)
                {
                    // Bounds a mid-transfer stall without capping the whole
                    // download: the clock restarts on every chunk that arrives.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stall.CancelAfter(StallTimeout);

                    int read;

                    try
                    {
                        read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new IOException(
                            $"The connection stopped sending data for {StallTimeout.TotalSeconds:F0} seconds.");
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    // Hashed as the bytes go past. A second pass over a 14 GiB
                    // file would cost minutes of disk read for nothing.
                    if (hashable)
                    {
                        hash.AppendData(buffer.AsSpan(0, read));
                    }

                    written += read;

                    estimate.Report(clock.Elapsed, written);

                    // Roughly ten updates a second is enough for a live rate.
                    if (clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(100))
                    {
                        continue;
                    }

                    lastReport = clock.Elapsed;
                    progress?.Report(new DownloadProgress
                    {
                        ComponentId = component.Id,
                        State = DownloadState.Downloading,
                        BytesSoFar = written,
                        TotalBytes = total,
                        BytesPerSecond = estimate.BytesPerSecond,
                        Remaining = estimate.Remaining,
                    });
                }
            }

            // Verify before promotion, never after. Until this passes the file
            // is a .part and nothing can mistake it for installed.
            var expected = integrity
                ?? ArtifactManifest.ForFileName(component.FileName)
                ?? new ArtifactIntegrity
                {
                    FileId = component.Id,
                    FileName = component.FileName,
                    SizeBytes = component.SizeBytes,
                };

            var computed = resumeFrom == 0 ? Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() : null;
            var verdict = ArtifactManifest.Verify(partial, expected, computed);

            if (!verdict.Passed)
            {
                TryDelete(partial);
                var bad = Failed(component, verdict.Detail);
                progress?.Report(bad);
                return bad;
            }

            File.Move(partial, destination, overwrite: true);

            var done = new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Installed,
                BytesSoFar = new FileInfo(destination).Length,
                TotalBytes = total,
                Detail = verdict.Detail,
                Sha256 = computed,
            };

            progress?.Report(done);
            return done;
        }
        catch (OperationCanceledException)
        {
            // The .part is kept, not deleted: it is exactly what a resume needs,
            // and it can never be mistaken for an installed file.

            var cancelled = new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Cancelled,
            };

            progress?.Report(cancelled);
            return cancelled;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(partial);
            var failure = Failed(component, $"{component.Name} could not be downloaded: {ex.Message}");
            progress?.Report(failure);
            return failure;
        }
    }

    /// <summary>
    /// Copies a local copy into the install folder, with the same
    /// verify-then-promote discipline a download gets: written to a .part,
    /// hashed as the bytes go past, and moved into place only once it passes.
    ///
    /// Progress is reported as Downloading. The state means "this row is moving"
    /// to everything that reads it, and inventing a second one for a transfer
    /// that happens to be local would make every caller handle both.
    /// </summary>
    private async Task<DownloadProgress> CopyIntoPlaceAsync(
        ModelComponent component,
        ArtifactIntegrity? integrity,
        string source,
        string destination,
        string partial,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var estimate = new TransferEstimate();
        var clock = Stopwatch.StartNew();

        try
        {
            var total = new FileInfo(source).Length;
            estimate.Reset(total);

            progress?.Report(new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Downloading,
                TotalBytes = total,
                Detail = $"Copying {component.Name} from {Path.GetDirectoryName(source)}",
            });

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var reader = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
            await using (var writer = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long written = 0;
                var lastReport = TimeSpan.Zero;

                while (true)
                {
                    var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer.AsSpan(0, read));

                    written += read;
                    estimate.Report(clock.Elapsed, written);

                    if (clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(100))
                    {
                        continue;
                    }

                    lastReport = clock.Elapsed;
                    progress?.Report(new DownloadProgress
                    {
                        ComponentId = component.Id,
                        State = DownloadState.Downloading,
                        BytesSoFar = written,
                        TotalBytes = total,
                        BytesPerSecond = estimate.BytesPerSecond,
                        Remaining = estimate.Remaining,
                    });
                }
            }

            var expected = integrity
                ?? ArtifactManifest.ForFileName(component.FileName)
                ?? new ArtifactIntegrity
                {
                    FileId = component.Id,
                    FileName = component.FileName,
                    SizeBytes = component.SizeBytes,
                };

            var computed = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var verdict = ArtifactManifest.Verify(partial, expected, computed);

            if (!verdict.Passed)
            {
                TryDelete(partial);
                return Report(progress, Failed(component, verdict.Detail));
            }

            File.Move(partial, destination, overwrite: true);

            return Report(progress, new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Installed,
                BytesSoFar = new FileInfo(destination).Length,
                TotalBytes = total,
                Detail = verdict.Detail,
                Sha256 = computed,
            });
        }
        catch (OperationCanceledException)
        {
            // Unlike a download's .part this is not a resume point -- the copy
            // always restarts from the beginning -- so it is not left behind to
            // be mistaken for one.
            TryDelete(partial);

            return Report(progress, new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Cancelled,
                TotalBytes = component.SizeBytes,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);

            return Report(
                progress,
                Failed(component, $"{component.Name} could not be copied from {source}: {ex.Message}"));
        }
    }

    private static DownloadProgress Installed(ModelComponent component, Presence presence) => new()
    {
        ComponentId = component.Id,
        State = DownloadState.Installed,
        BytesSoFar = presence.BytesOnDisk,
        TotalBytes = presence.BytesOnDisk,
        Detail = presence.Explain(component.Name),
    };

    private static DownloadProgress Report(IProgress<DownloadProgress>? progress, DownloadProgress report)
    {
        progress?.Report(report);
        return report;
    }

    private static DownloadProgress Failed(ModelComponent component, string reason) => new()
    {
        ComponentId = component.Id,
        State = DownloadState.Failed,
        TotalBytes = component.SizeBytes,
        Failure = reason,
    };


    /// <summary>
    /// How much of a previous attempt can be kept. A .part at or beyond the
    /// expected length is not a resume point but an interrupted promotion, and
    /// starting over is cheaper than reasoning about what it contains.
    /// </summary>
    private static long ResumeOffset(string partial, long expected)
    {
        try
        {
            if (!File.Exists(partial))
            {
                return 0;
            }

            var length = new FileInfo(partial).Length;
            return expected > 0 && length >= expected ? 0 : length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Names what the status actually means, rather than printing a number.</summary>
    private static string Explain(HttpStatusCode status, string name) => status switch
    {
        HttpStatusCode.NotFound => $"{name} is no longer available at its download link (404).",
        HttpStatusCode.Forbidden => $"Access to {name} was refused (403). Its sharing link may have been withdrawn.",
        HttpStatusCode.TooManyRequests => $"The download quota for {name} is exhausted (429). It resets after about a day.",
        _ => $"The server answered {(int)status} for {name}.",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover .part is harmless; it is overwritten on the next run.
        }
    }
}
