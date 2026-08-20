using System.Windows;
using System.Windows.Threading;
using BetterTranslator.App.Notifications;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Updates.Ipc;

namespace BetterTranslator.App;

public partial class App : Application
{
    private static readonly TimeSpan ActivationWatchdog = TimeSpan.FromSeconds(20);

    private AppInstance? _instance;
    private ToastActivatorHost? _activator;
    private UpdateNotifications? _notifications;
    private DispatcherTimer? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShellIdentity.Apply();

        var verb = e.Args.Length > 0 ? e.Args[0] : string.Empty;

        // Each of these runs the application with no window at all. The main
        // window is opened at the end of this method rather than by a StartupUri
        // in App.xaml, because a StartupUri is navigated to after OnStartup
        // returns whatever this does, and the property refuses to be cleared:
        // assigning null to it throws, which took every one of these verbs down
        // before it reached its handler.
        switch (verb)
        {
            case UpdaterGateway.InstallVerb:
            case UpdaterGateway.UninstallVerb:
                Shutdown(UpdaterGateway.RunElevatedVerb(verb));
                return;

            case UpdaterGateway.FinishVerb:
                Shutdown(UpdaterGateway.FinishAsync(e.Args).GetAwaiter().GetResult());
                return;

            case UpdateNotifications.NotifyVerb:
                Shutdown(RaisePendingNotice());
                return;

            case ShellIdentity.ActivatedVerb:
                WaitForActivation();
                return;
        }

        UpdaterGateway.ForgetSupersededBuilds();

        _notifications = UpdateNotifications.ForApp(new UpdaterGateway());
        _activator = ToastActivatorHost.Register(OnNotificationActivated);

        _instance = new AppInstance();
        _instance.Serve(AnswerAsync);

        base.OnStartup(e);

        _ = Task.Run(PrepareNotifications);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainWindowViewModel shell)
        {
            shell.AgentServer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        _activator?.Dispose();
        _instance?.Dispose();

        base.OnExit(e);
    }

    private int RaisePendingNotice()
    {
        var notifications = UpdateNotifications.ForApp(new UpdaterGateway());

        notifications.EnsureRegistered();

        return notifications.RaisePending().Raised ? 0 : 1;
    }

    private void WaitForActivation()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _notifications = UpdateNotifications.ForApp(new UpdaterGateway());
        _activator = ToastActivatorHost.Register(OnColdActivation);

        if (_activator is null)
        {
            Shutdown(2);
            return;
        }

        _watchdog = new DispatcherTimer(
            ActivationWatchdog,
            DispatcherPriority.Background,
            (_, _) => Shutdown(0),
            Dispatcher);

        _watchdog.Start();
    }

    private void OnColdActivation(string arguments)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            _watchdog?.Stop();

            try
            {
                if (RunningInstance.Exists())
                {
                    await new AppInstanceClient()
                        .AskAsync(AppInstanceVerb.Activate, arguments, CancellationToken.None)
                        .ConfigureAwait(true);
                }
                else if (_notifications is { } notifications)
                {
                    await notifications
                        .ActivateAsync(arguments, inTheRunningApp: false, CancellationToken.None)
                        .ConfigureAwait(true);
                }
            }
            finally
            {
                Shutdown(0);
            }
        });
    }

    private void OnNotificationActivated(string arguments)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_notifications is { } notifications)
            {
                await notifications
                    .ActivateAsync(arguments, inTheRunningApp: true, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        });
    }

    private async Task<AppInstanceReply> AnswerAsync(AppInstanceRequest request, CancellationToken cancellationToken)
    {
        switch (request.Verb)
        {
            case AppInstanceVerb.Notify:
                var raised = _notifications?.RaisePending();

                return new AppInstanceReply(raised?.Raised ?? false, raised?.Detail ?? "Notifications are not ready.");

            case AppInstanceVerb.Activate:
                if (_notifications is not { } notifications)
                {
                    return AppInstanceReply.Refused("Notifications are not ready.");
                }

                return await notifications
                    .ActivateAsync(request.Arguments, inTheRunningApp: true, cancellationToken)
                    .ConfigureAwait(false);

            case AppInstanceVerb.Close:
                _ = Dispatcher.BeginInvoke(() => Shutdown(0));

                return AppInstanceReply.Done("Closing.");

            default:
                return AppInstanceReply.Done("Running.");
        }
    }

    private void PrepareNotifications()
    {
        if (_notifications is not { } notifications)
        {
            return;
        }

        notifications.EnsureRegistered();
        notifications.RaisePending();
    }
}
