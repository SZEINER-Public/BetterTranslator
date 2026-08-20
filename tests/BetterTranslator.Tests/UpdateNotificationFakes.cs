using System.IO;
using BetterTranslator.App.Notifications;
using BetterTranslator.Updates.Notifications;

namespace BetterTranslator.Tests;

internal sealed class FakeRegistryStore : IRegistryStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Writes { get; } = [];

    public List<string> Removals { get; } = [];

    public string? Read(string key) => Values.GetValueOrDefault(key);

    public void Write(string key, string value)
    {
        Values[key] = value;
        Writes.Add(key);
    }

    public void Remove(string key)
    {
        Removals.Add(key);

        foreach (var stored in Values.Keys.Where(k => k.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Values.Remove(stored);
        }
    }
}

internal sealed class FakeShortcutStore : IShortcutStore
{
    public Dictionary<string, string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Created { get; } = [];

    public List<string> Removed { get; } = [];

    public bool Refuse { get; set; }

    public string? AppUserModelId { get; private set; }

    public Guid Activator { get; private set; }

    public string? TargetOf(string path) => Targets.GetValueOrDefault(path);

    public bool Create(string path, string target, string appUserModelId, Guid activator)
    {
        if (Refuse)
        {
            return false;
        }

        Targets[path] = target;
        AppUserModelId = appUserModelId;
        Activator = activator;
        Created.Add(path);

        return true;
    }

    public void Remove(string path)
    {
        Targets.Remove(path);
        Removed.Add(path);
    }
}

internal sealed class FakeNotificationChannel : INotificationChannel
{
    public List<PendingUpdateNotice> Shown { get; } = [];

    public int Withdrawals { get; private set; }

    public bool Refuse { get; set; }

    public bool TryShow(PendingUpdateNotice notice, string installedVersion, out string detail)
    {
        if (Refuse)
        {
            detail = "Windows would not raise the notification.";

            return false;
        }

        Shown.Add(notice);
        detail = "Raised.";

        return true;
    }

    public void Withdraw() => Withdrawals++;
}

internal sealed class FakeInstallCoordinator : IInstallCoordinator
{
    public bool AnotherInstanceIsRunning { get; set; }

    public bool LocationIsWritable { get; set; } = true;

    public bool ServiceIsInstalled { get; set; }

    public bool CloseSucceeds { get; set; } = true;

    public int CloseRequests { get; private set; }

    public int HandOvers { get; private set; }

    public int ServiceApplies { get; private set; }

    public int Elevations { get; private set; }

    public int Launches { get; private set; }

    public void LaunchApplication() => Launches++;

    public Task<bool> AskRunningInstanceToCloseAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        CloseRequests++;

        return Task.FromResult(CloseSucceeds);
    }

    public Task<InstallOutcome> HandOverAsync(CancellationToken cancellationToken)
    {
        HandOvers++;

        return Task.FromResult(new InstallOutcome(true, "Handed over."));
    }

    public Task<InstallOutcome> AskServiceToApplyAsync(CancellationToken cancellationToken)
    {
        ServiceApplies++;

        return Task.FromResult(new InstallOutcome(true, "The service applied it."));
    }

    public Task<InstallOutcome> ElevateForServiceAsync(CancellationToken cancellationToken)
    {
        Elevations++;

        return Task.FromResult(new InstallOutcome(true, "Elevated once and applied."));
    }
}

internal sealed class TemporaryUserState : IDisposable
{
    public TemporaryUserState()
    {
        Root = Path.Combine(Path.GetTempPath(), "bt-notify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Notified = new NotifiedStore(Path.Combine(Root, "notified.json"));
    }

    public string Root { get; }

    public NotifiedStore Notified { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
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
