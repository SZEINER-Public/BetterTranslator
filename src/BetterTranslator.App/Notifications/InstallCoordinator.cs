using System.Windows;
using BetterTranslator.App.Services;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Ipc;
using BetterTranslator.Updates.Service;

namespace BetterTranslator.App.Notifications;

public sealed record InstallOutcome(bool Started, string Detail)
{
    public static InstallOutcome Refused(string detail) => new(false, detail);
}

public interface IInstallCoordinator
{
    bool AnotherInstanceIsRunning { get; }

    bool LocationIsWritable { get; }

    bool ServiceIsInstalled { get; }

    Task<bool> AskRunningInstanceToCloseAsync(TimeSpan wait, CancellationToken cancellationToken);

    Task<InstallOutcome> HandOverAsync(CancellationToken cancellationToken);

    Task<InstallOutcome> AskServiceToApplyAsync(CancellationToken cancellationToken);

    Task<InstallOutcome> ElevateForServiceAsync(CancellationToken cancellationToken);

    void LaunchApplication();
}

public sealed class InstallCoordinator(IUpdaterHost updater, bool inTheRunningApp) : IInstallCoordinator
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly UpdaterPipeClient _service = new();

    public bool AnotherInstanceIsRunning => !inTheRunningApp && RunningInstance.Exists();

    public bool LocationIsWritable =>
        Environment.ProcessPath is { Length: > 0 } executable && InstallLocation.IsWritable(executable);

    public bool ServiceIsInstalled => updater.State() != ServiceState.NotInstalled;

    public async Task<bool> AskRunningInstanceToCloseAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var answered = await new AppInstanceClient()
            .AskAsync(AppInstanceVerb.Close, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (answered is not { Ok: true })
        {
            return !RunningInstance.Exists();
        }

        var deadline = DateTimeOffset.UtcNow + wait;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!RunningInstance.Exists())
            {
                return true;
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }

        return !RunningInstance.Exists();
    }

    public async Task<InstallOutcome> HandOverAsync(CancellationToken cancellationToken)
    {
        var outcome = await updater.HandOverToStagedBuildAsync(cancellationToken).ConfigureAwait(false);

        if (!outcome.Ok)
        {
            return InstallOutcome.Refused(outcome.Detail);
        }

        if (inTheRunningApp)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => Application.Current?.Shutdown());
        }

        return new InstallOutcome(true, "The staged build takes over once this one closes.");
    }

    public async Task<InstallOutcome> AskServiceToApplyAsync(CancellationToken cancellationToken)
    {
        var answered = await _service.AskAsync(UpdaterVerb.Apply, cancellationToken).ConfigureAwait(false);

        return answered is { Ok: true }
            ? new InstallOutcome(true, answered.Detail)
            : InstallOutcome.Refused(answered?.Detail ?? "The updater service did not answer.");
    }

    public void LaunchApplication()
    {
        if (inTheRunningApp || Environment.ProcessPath is not { Length: > 0 } executable)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task<InstallOutcome> ElevateForServiceAsync(CancellationToken cancellationToken)
    {
        var elevated = await updater.EnableAsync(cancellationToken).ConfigureAwait(false);

        if (!elevated.Ok)
        {
            return InstallOutcome.Refused(elevated.Detail.Length > 0
                ? elevated.Detail
                : "The administrator prompt was refused, so nothing was replaced.");
        }

        return await AskServiceToApplyAsync(cancellationToken).ConfigureAwait(false);
    }
}
