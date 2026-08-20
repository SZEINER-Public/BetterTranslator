using BetterTranslator.App.Services;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Ipc;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Notifications;

namespace BetterTranslator.App.Notifications;

public sealed record NotificationOutcome(bool Raised, string Detail);

public sealed class UpdateNotifications(
    INotificationChannel channel,
    ShellRegistration registration,
    PendingNoticeStore pending,
    NotifiedStore notified,
    BuildIdentity installed,
    Func<bool, IInstallCoordinator> coordinator,
    IUpdateLog? log = null)
{
    public const string NotifyVerb = "--notify-update";

    public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(20);

    private readonly IUpdateLog _log = log ?? NullUpdateLog.Instance;

    public RegistrationOutcome EnsureRegistered()
    {
        var outcome = registration.Register();

        if (outcome.Changed || !outcome.Registered)
        {
            _log.Write($"Notification registration: {outcome.Detail}");
        }

        return outcome;
    }

    public NotificationOutcome RaisePending()
    {
        var notice = pending.Read();

        if (notice is null)
        {
            return new NotificationOutcome(false, "Nothing is waiting to be announced.");
        }

        if (ReleaseIdentity.Same(notice.Identity, ReleaseIdentity.Of(installed.Version, installed.Commit)))
        {
            pending.Clear();

            return new NotificationOutcome(false, "The waiting release is the build already running.");
        }

        if (notified.WasShown(notice.Identity))
        {
            return new NotificationOutcome(false, "This release has already been announced once.");
        }

        if (!registration.IsRegistered && !EnsureRegistered().Registered)
        {
            _log.Write("The notification could not be raised because registration failed. The app carries the notice instead.");

            return new NotificationOutcome(false, "Registration failed, so the notice waits for the application window.");
        }

        if (!channel.TryShow(notice, installed.Version, out var detail))
        {
            _log.Write("The notification could not be raised: " + detail);

            return new NotificationOutcome(false, detail);
        }

        notified.Record(notice.Identity, dismissed: false);

        return new NotificationOutcome(true, detail);
    }

    public async Task<AppInstanceReply> ActivateAsync(
        string arguments,
        bool inTheRunningApp,
        CancellationToken cancellationToken)
    {
        if (!ToastActivationPayload.TryParse(arguments, out var activation))
        {
            _log.Write("A notification activation was refused because its payload was not readable.");

            return AppInstanceReply.Refused("That notification payload is not one this accepts.");
        }

        if (activation.Version.Length > 0)
        {
            notified.Record(activation.Identity, dismissed: activation.Action == ToastAction.Later);
        }

        switch (activation.Action)
        {
            case ToastAction.Later:
                channel.Withdraw();

                return AppInstanceReply.Done("The update waits until you are ready.");

            case ToastAction.Install:
                var outcome = await InstallAsync(inTheRunningApp, cancellationToken).ConfigureAwait(false);

                if (outcome.Started)
                {
                    channel.Withdraw();
                }
                else
                {
                    _log.Write("The install from a notification did not run: " + outcome.Detail);
                }

                return new AppInstanceReply(outcome.Started, outcome.Detail);

            default:
                if (!inTheRunningApp)
                {
                    coordinator(inTheRunningApp).LaunchApplication();
                }

                return AppInstanceReply.Done("Opened.");
        }
    }

    public void Remove()
    {
        channel.Withdraw();
        registration.Remove();
    }

    private async Task<InstallOutcome> InstallAsync(bool inTheRunningApp, CancellationToken cancellationToken)
    {
        var install = coordinator(inTheRunningApp);
        var route = InstallLocation.Decide(install.LocationIsWritable, install.ServiceIsInstalled);

        if (install.AnotherInstanceIsRunning)
        {
            var closed = await install
                .AskRunningInstanceToCloseAsync(CloseWait, cancellationToken)
                .ConfigureAwait(false);

            if (!closed)
            {
                return InstallOutcome.Refused(
                    "BetterTranslator is still open, so nothing was replaced. The download is kept and the update is "
                    + "offered again later.");
            }
        }

        return route switch
        {
            InstallRoute.Direct => await install.HandOverAsync(cancellationToken).ConfigureAwait(false),
            InstallRoute.ServiceHandoff => await install.AskServiceToApplyAsync(cancellationToken).ConfigureAwait(false),
            _ => await install.ElevateForServiceAsync(cancellationToken).ConfigureAwait(false),
        };
    }

    public static UpdateNotifications ForApp(IUpdaterHost updater, UpdatePaths? paths = null)
    {
        var root = paths ?? new UpdatePaths();
        var executable = Environment.ProcessPath ?? string.Empty;

        return new UpdateNotifications(
            new ToastChannel(ShellIdentity.AppUserModelId),
            new ShellRegistration(new UserRegistryStore(), new StartMenuShortcut(), executable),
            new PendingNoticeStore(root),
            NotifiedStore.ForCurrentUser(),
            BuildIdentity.Current,
            inTheRunningApp => new InstallCoordinator(updater, inTheRunningApp),
            new RollingFileLog(UserLogFolder, "notifications.log"));
    }

    public static void RemoveRegistration()
    {
        var executable = Environment.ProcessPath ?? string.Empty;

        new ToastChannel(ShellIdentity.AppUserModelId).Withdraw();
        new ShellRegistration(new UserRegistryStore(), new StartMenuShortcut(), executable).Remove();
    }

    public static string UserLogFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterTranslator",
        "updates",
        "logs");
}
