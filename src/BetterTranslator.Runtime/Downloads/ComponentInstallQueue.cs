using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

public sealed class ComponentInstallQueue(DownloadManager downloads, ModelResolver resolver)
{
    private readonly Dictionary<string, PendingInstall> _pending = new(StringComparer.OrdinalIgnoreCase);

    public Presence Resolve(ModelComponent component) => resolver.Resolve(component);

    public bool IsPending(string componentId)
    {
        lock (_pending)
        {
            return _pending.ContainsKey(componentId);
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
            var already = new DownloadProgress
            {
                ComponentId = component.Id,
                State = DownloadState.Installed,
                BytesSoFar = presence.BytesOnDisk,
                TotalBytes = presence.BytesOnDisk,
                Detail = presence.Explain(component.Name),
            };

            progress?.Report(already);
            return already;
        }

        PendingInstall entry;
        bool owned;

        lock (_pending)
        {
            if (_pending.TryGetValue(component.Id, out var running))
            {
                entry = running;
                owned = false;
            }
            else
            {
                entry = new PendingInstall();
                _pending[component.Id] = entry;
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
            var result = await downloads.DownloadAsync(component, entry, cancellationToken).ConfigureAwait(false);
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
                _pending.Remove(component.Id);
            }
        }
    }

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
