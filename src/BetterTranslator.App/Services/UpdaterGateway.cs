using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Ipc;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Notifications;
using BetterTranslator.Updates.Payload;
using BetterTranslator.Updates.Releases;
using BetterTranslator.Updates.Service;

namespace BetterTranslator.App.Services;

public sealed record ElevationOutcome(bool Ok, string Detail);

public interface IUpdaterHost
{
    BuildIdentity Installed { get; }

    ServiceState State();

    Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken);

    Task<UpdateStatus?> ReadyAsync(CancellationToken cancellationToken);

    Task<ElevationOutcome> EnableAsync(CancellationToken cancellationToken);

    Task<ElevationOutcome> DisableAsync(CancellationToken cancellationToken);

    Task<ElevationOutcome> HandOverToStagedBuildAsync(CancellationToken cancellationToken);
}

public sealed class UpdaterGateway : IUpdaterHost
{
    public const string InstallVerb = "--install-updater";
    public const string UninstallVerb = "--uninstall-updater";
    public const string FinishVerb = "--finish-update";

    private readonly UpdatePaths _paths;
    private readonly UpdaterPipeClient _pipe = new();

    public UpdaterGateway()
        : this(new UpdatePaths())
    {
    }

    public UpdaterGateway(UpdatePaths paths) => _paths = paths;

    public BuildIdentity Installed => BuildIdentity.Current;

    public ServiceState State() => ServiceControl.Query(UpdatePaths.ServiceName);

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        if (State() == ServiceState.Running)
        {
            var answered = await _pipe.AskAsync(UpdaterVerb.Check, cancellationToken).ConfigureAwait(false);

            if (answered?.Status is { } status)
            {
                return status;
            }
        }

        return await InProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateStatus?> ReadyAsync(CancellationToken cancellationToken)
    {
        if (State() == ServiceState.Running)
        {
            var answered = await _pipe.AskAsync(UpdaterVerb.Status, cancellationToken).ConfigureAwait(false);

            if (answered?.Status is { Ready: true } status)
            {
                return status;
            }
        }

        var staged = new StagedUpdateStore(_paths.ReadyFile).Read();

        if (staged is not null && IsThisBuild(staged))
        {
            return null;
        }

        return staged is null
            ? null
            : new UpdateStatus(
                UpdateOutcome.UpdateAvailable,
                $"{staged.Version} is downloaded and verified.",
                Installed.Version,
                Installed.HasCommit ? Installed.Commit : string.Empty,
                staged.Version,
                staged.Commit,
                Ready: true,
                staged.StagedUtc);
    }

    private bool IsThisBuild(StagedUpdate staged) =>
        string.Equals(staged.Version, Installed.Version, StringComparison.OrdinalIgnoreCase)
        && (staged.Commit.Length == 0
            || !Installed.HasCommit
            || UpdateComparer.SameCommit(staged.Commit, Installed.Commit));

    public Task<ElevationOutcome> EnableAsync(CancellationToken cancellationToken) =>
        ElevateAsync(InstallVerb, cancellationToken);

    public async Task<ElevationOutcome> DisableAsync(CancellationToken cancellationToken)
    {
        var outcome = await ElevateAsync(UninstallVerb, cancellationToken).ConfigureAwait(false);

        if (outcome.Ok)
        {
            new PendingNoticeStore(_paths).Clear();
            BetterTranslator.App.Notifications.UpdateNotifications.RemoveRegistration();
        }

        return outcome;
    }

    public async Task<ElevationOutcome> HandOverToStagedBuildAsync(CancellationToken cancellationToken)
    {
        var installed = Environment.ProcessPath;

        if (string.IsNullOrEmpty(installed) || !File.Exists(installed))
        {
            return new ElevationOutcome(false, "The installed application could not be located on disk.");
        }

        var staged = new StagedUpdateStore(_paths.ReadyFile).Read();

        if (staged is null)
        {
            return new ElevationOutcome(false, "Nothing is staged to install.");
        }

        var verdict = await PayloadVerifier
            .VerifyAsync(staged.File, staged.Sha256, staged.SizeBytes, cancellationToken)
            .ConfigureAwait(false);

        if (!verdict.Accepted)
        {
            return new ElevationOutcome(false, verdict.Detail);
        }

        var start = new ProcessStartInfo(staged.File)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add(FinishVerb);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(installed);

        try
        {
            using var process = Process.Start(start);

            return process is null
                ? new ElevationOutcome(false, "Windows did not start the staged build.")
                : new ElevationOutcome(true, string.Empty);
        }
        catch (Win32Exception ex)
        {
            return new ElevationOutcome(false, $"The staged build would not start: {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            return new ElevationOutcome(false, "The staged build would not start.");
        }
    }

    public static async Task<int> FinishAsync(string[] arguments)
    {
        if (arguments.Length < 3
            || !int.TryParse(arguments[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid))
        {
            return 2;
        }

        var installed = arguments[2];
        var paths = new UpdatePaths();
        var log = new RollingFileLog(paths.LogFolder, "handover.log");

        await WaitForExitAsync(pid).ConfigureAwait(false);

        var staged = new StagedUpdateStore(paths.ReadyFile).Read();
        var applied = 0;

        if (staged is not null)
        {
            var verdict = await new UpdateApplier(paths, log)
                .ApplyAsync(installed, staged, staged.Sha256, CancellationToken.None)
                .ConfigureAwait(false);

            log.Write($"Hand over to {staged.Version}: {verdict.Detail}");
            applied = verdict.State == ApplyState.Applied ? 0 : 2;
        }

        Relaunch(installed);

        return applied;
    }

    private static async Task WaitForExitAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Relaunch(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static int RunElevatedVerb(string verb)
    {
        var paths = new UpdatePaths();
        var log = new RollingFileLog(paths.LogFolder, "install.log");
        var installer = new UpdaterServiceInstaller(paths, log);

        var outcome = verb == UninstallVerb
            ? installer.Remove()
            : installer.Install(UpdaterServiceInstaller.SourceFolder, Environment.ProcessPath ?? string.Empty);

        log.Write($"{verb}: {outcome.Detail}");

        return outcome.Ok ? 0 : 2;
    }

    public static void ForgetSupersededBuilds()
    {
        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        new UpdateApplier(new UpdatePaths()).RemoveSupersededBuilds(executable);
    }

    private async Task<UpdateStatus> InProcessAsync(CancellationToken cancellationToken)
    {
        using var http = GitHubReleaseClient.CreateHttpClient();

        var workflow = new UpdateWorkflow(new GitHubReleaseClient(http, _paths), http, _paths);

        return await workflow.CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ElevationOutcome> ElevateAsync(string verb, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            return new ElevationOutcome(false, "The installed application could not be located on disk.");
        }

        var start = new ProcessStartInfo(executable, verb)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return new ElevationOutcome(false, "Windows did not start the elevated step.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0
                ? new ElevationOutcome(true, string.Empty)
                : new ElevationOutcome(false, "The elevated step did not finish. The updater log has the reason.");
        }
        catch (Win32Exception)
        {
            return new ElevationOutcome(false, "The administrator prompt was refused, so nothing changed.");
        }
        catch (InvalidOperationException)
        {
            return new ElevationOutcome(false, "The elevated step could not be started.");
        }
    }
}
