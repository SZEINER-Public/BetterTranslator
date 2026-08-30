using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

public sealed class ComponentInstallQueue
{
    public const int VerificationAttempts = 3;

    private static readonly TimeSpan BetweenVerifications = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, PendingInstall> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly DownloadManager _downloads;
    private readonly ModelResolver _resolver;
    private readonly IInstallVerifier _verifier;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ComponentInstallQueue(DownloadManager downloads, ModelResolver resolver, InstallPaths paths)
        : this(downloads, resolver, new ComponentInstallVerifier(paths))
    {
    }

    public ComponentInstallQueue(DownloadManager downloads, ModelResolver resolver, IInstallVerifier verifier)
        : this(downloads, resolver, verifier, (wait, token) => Task.Delay(wait, token))
    {
    }

    internal ComponentInstallQueue(
        DownloadManager downloads,
        ModelResolver resolver,
        IInstallVerifier verifier,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _downloads = downloads;
        _resolver = resolver;
        _verifier = verifier;
        _delay = delay;
    }

    /// <summary>
    /// What one install is. The version is half of it: a component rebuilt
    /// under the same id is a different artifact and must not be answered by
    /// the operation that is fetching the old one.
    /// </summary>
    public static string KeyFor(ModelComponent component) => component.Id + "@" + component.Version;

    public Presence Resolve(ModelComponent component) => _resolver.Resolve(component);

    public bool IsPending(ModelComponent component) => IsPending(KeyFor(component));

    public bool IsPending(string key)
    {
        lock (_pending)
        {
            return _pending.ContainsKey(key);
        }
    }

    public async Task<DownloadProgress> EnqueueAsync(
        ModelComponent component,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var presence = Resolve(component);

        if (presence.Reason == PresenceReason.InstalledHere)
        {
            var verified = await _verifier.VerifyAsync(component, cancellationToken).ConfigureAwait(false);

            var already = verified.Passed
                ? new DownloadProgress
                {
                    ComponentId = component.Id,
                    State = DownloadState.Installed,
                    BytesSoFar = presence.BytesOnDisk,
                    TotalBytes = presence.BytesOnDisk,
                    Detail = presence.Explain(component.Name),
                }
                : Failed(component, verified.Detail);

            progress?.Report(already);
            return already;
        }

        var key = KeyFor(component);

        PendingInstall entry;
        bool owned;

        lock (_pending)
        {
            if (_pending.TryGetValue(key, out var running))
            {
                entry = running;
                owned = false;
            }
            else
            {
                entry = new PendingInstall();
                _pending[key] = entry;
                owned = true;
            }
        }

        entry.Follow(progress);

        if (!owned)
        {
            return await entry.Completion.ConfigureAwait(false);
        }

        try
        {
            var result = await InstallAndVerifyAsync(component, entry, cancellationToken).ConfigureAwait(false);

            entry.Complete(result);
            return result;
        }
        catch (Exception failure)
        {
            entry.Fail(failure);
            throw;
        }
        finally
        {
            lock (_pending)
            {
                _pending.Remove(key);
            }
        }
    }

    private async Task<DownloadProgress> InstallAndVerifyAsync(
        ModelComponent component,
        PendingInstall entry,
        CancellationToken cancellationToken)
    {
        var result = await _downloads.DownloadAsync(component, entry, cancellationToken).ConfigureAwait(false);

        if (result.State != DownloadState.Installed)
        {
            return result;
        }

        InstallVerification verification = InstallVerification.Fail("The install was never verified.", []);

        for (var attempt = 1; attempt <= VerificationAttempts; attempt++)
        {
            verification = await _verifier.VerifyAsync(component, cancellationToken).ConfigureAwait(false);

            if (verification.Passed)
            {
                return result with { Detail = verification.Detail };
            }

            if (attempt < VerificationAttempts)
            {
                await _delay(BetweenVerifications, cancellationToken).ConfigureAwait(false);
            }
        }

        // Reported, never reinstalled. Re-running the install on a verification
        // that keeps failing is what turned one component into four downloads,
        // and it cannot fix a file that is already exactly where it was put.
        var failure = Failed(
            component,
            $"{verification.Detail} Checked {VerificationAttempts} times. "
            + "The download finished, so this is not a transfer problem: check that nothing is quarantining the file.");

        entry.Report(failure);

        return failure;
    }

    private static DownloadProgress Failed(ModelComponent component, string reason) => new()
    {
        ComponentId = component.Id,
        State = DownloadState.Failed,
        TotalBytes = component.InstallBytes,
        Failure = reason,
    };

    private sealed class PendingInstall : IProgress<DownloadProgress>
    {
        private readonly List<IProgress<DownloadProgress>> _followers = [];

        private readonly TaskCompletionSource<DownloadProgress> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DownloadProgress> Completion => _completion.Task;

        public void Follow(IProgress<DownloadProgress>? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (_followers)
            {
                _followers.Add(progress);
            }
        }

        public void Report(DownloadProgress value)
        {
            IProgress<DownloadProgress>[] listening;

            lock (_followers)
            {
                listening = [.. _followers];
            }

            foreach (var follower in listening)
            {
                follower.Report(value);
            }
        }

        public void Complete(DownloadProgress result) => _completion.TrySetResult(result);

        public void Fail(Exception failure) => _completion.TrySetException(failure);
    }
}
