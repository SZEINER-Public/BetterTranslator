using BetterTranslator.App.Services;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Releases;
using BetterTranslator.Updates.Service;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdaterHost _host;

    private bool _syncing;

    private Task _pending = Task.CompletedTask;

    internal Task Pending => _pending;

    public UpdatesViewModel()
        : this(new UpdaterGateway())
    {
    }

    public UpdatesViewModel(IUpdaterHost host)
    {
        _host = host;
        InstalledVersion = host.Installed.Version;
        InstalledLabel = Describe(host.Installed.Version, host.Installed.HasCommit ? host.Installed.Commit : string.Empty);
        Channel = host.Installed.Channel;
    }

    public string InstalledVersion { get; }

    [ObservableProperty]
    public partial bool AutomaticUpdates { get; set; }

    [ObservableProperty]
    public partial string ServiceStatus { get; set; } = "Reading the service state.";

    [ObservableProperty]
    public partial string InstalledLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatestLabel { get; set; } = "Not checked yet.";

    [ObservableProperty]
    public partial string CheckStatus { get; set; } = "Press Check for updates to ask GitHub for the latest release.";

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial bool UpdateReady { get; set; }

    [ObservableProperty]
    public partial bool NoticeDismissed { get; set; }

    public string Channel { get; }

    public bool CanInteract => !IsWorking;

    public bool ShowNotice => UpdateReady && !NoticeDismissed;

    partial void OnIsWorkingChanged(bool value) => OnPropertyChanged(nameof(CanInteract));

    partial void OnUpdateReadyChanged(bool value) => OnPropertyChanged(nameof(ShowNotice));

    partial void OnNoticeDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowNotice));

    [RelayCommand]
    private void Dismiss() => NoticeDismissed = true;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ReadServiceState();

        var ready = await _host.ReadyAsync(cancellationToken).ConfigureAwait(true);

        if (ready is not null)
        {
            UpdateReady = true;
            LatestLabel = Describe(ready.LatestVersion, ready.LatestCommit);
            CheckStatus = ready.Detail;
        }
    }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        if (_host.State() == ServiceState.NotInstalled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (UpdateReady || IsWorking)
                {
                    continue;
                }

                await LoadAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsWorking)
        {
            return;
        }

        IsWorking = true;
        CheckStatus = "Asking GitHub for the latest release.";

        try
        {
            var status = await _host.CheckAsync(CancellationToken.None).ConfigureAwait(true);

            LatestLabel = status.LatestVersion.Length == 0
                ? "The latest release could not be read."
                : Describe(status.LatestVersion, status.LatestCommit);

            CheckStatus = status.Outcome switch
            {
                UpdateOutcome.UpToDate => "This is the latest build. " + status.Detail,
                UpdateOutcome.UpdateAvailable => "An update is available. " + status.Detail,
                _ => "The check did not finish. " + status.Detail,
            };

            UpdateReady = status.Ready;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LatestLabel = "The latest release could not be read.";
            CheckStatus = "The check did not finish. " + ex.Message;
        }
        finally
        {
            IsWorking = false;
            ReadServiceState();
        }
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsWorking)
        {
            return;
        }

        IsWorking = true;

        try
        {
            var outcome = await _host.HandOverToStagedBuildAsync(CancellationToken.None).ConfigureAwait(true);

            if (!outcome.Ok)
            {
                CheckStatus = outcome.Detail;
                UpdateReady = false;
                return;
            }

            System.Windows.Application.Current?.Shutdown();
        }
        finally
        {
            IsWorking = false;
        }
    }

    partial void OnAutomaticUpdatesChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        _pending = SwitchAsync(value);
    }

    private async Task SwitchAsync(bool wanted)
    {
        IsWorking = true;
        ServiceStatus = wanted ? "Registering the updater." : "Removing the updater.";

        try
        {
            var outcome = wanted
                ? await _host.EnableAsync(CancellationToken.None).ConfigureAwait(true)
                : await _host.DisableAsync(CancellationToken.None).ConfigureAwait(true);

            var state = _host.State();
            var installed = state != ServiceState.NotInstalled;

            if (installed != wanted)
            {
                Set(installed);
                ServiceStatus = outcome.Detail.Length > 0
                    ? outcome.Detail
                    : "Windows did not make that change, so nothing was altered.";

                return;
            }

            ServiceStatus = Explain(state);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void ReadServiceState()
    {
        var state = _host.State();

        Set(state != ServiceState.NotInstalled);
        ServiceStatus = Explain(state);
    }

    private void Set(bool value)
    {
        _syncing = true;

        try
        {
            AutomaticUpdates = value;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static string Explain(ServiceState state) => state switch
    {
        ServiceState.NotInstalled =>
            "No updater is registered on this machine. Nothing runs in the background while this is off.",
        ServiceState.Running =>
            "The updater is running. It looks for a new release a few times a day and verifies what it downloads.",
        ServiceState.Starting => "The updater is starting.",
        ServiceState.Stopping => "The updater is stopping.",
        ServiceState.Stopped => "The updater is registered but not running. Windows starts it shortly after you sign in.",
        ServiceState.Paused => "The updater is paused.",
        _ => "The updater state could not be read from Windows.",
    };

    private static string Describe(string version, string commit)
    {
        if (version.Length == 0)
        {
            return "Unknown.";
        }

        return commit.Length == 0
            ? version + ", built from an unrecorded commit"
            : version + "+" + (commit.Length > 7 ? commit[..7] : commit);
    }
}
