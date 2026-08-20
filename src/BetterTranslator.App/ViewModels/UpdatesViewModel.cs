using BetterTranslator.App.Services;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Releases;
using BetterTranslator.Updates.Service;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdaterHost _host;

    private readonly IUpdateLog _log;

    private bool _syncing;

    private Task _pending = Task.CompletedTask;

    internal Task Pending => _pending;

    public UpdatesViewModel()
        : this(new UpdaterGateway(), new RollingFileLog(UpdatePaths.ForCurrentUser().LogFolder, "updates.log"))
    {
    }

    public UpdatesViewModel(IUpdaterHost host, IUpdateLog? log = null)
    {
        _host = host;
        _log = log ?? NullUpdateLog.Instance;
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
    public partial string CheckStatus { get; set; } =
        "This build was checked against GitHub when the application started. Press Check for updates to ask again.";

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial bool UpdateReady { get; set; }

    [ObservableProperty]
    public partial bool NoticeDismissed { get; set; }

    /// <summary>
    /// A check found a newer release and nothing has been downloaded for it yet.
    /// </summary>
    [ObservableProperty]
    public partial bool UpdateFound { get; set; }

    public string Channel { get; }

    public bool CanInteract => !IsWorking;

    public bool ShowNotice => (UpdateReady || UpdateFound || IsDownloading) && !NoticeDismissed;

    public bool CanDownload => UpdateFound && !UpdateReady && !IsWorking;

    /// <summary>The release a check found, with no commit on it.</summary>
    [ObservableProperty]
    public partial string LatestVersion { get; set; } = string.Empty;

    /// <summary>A payload is arriving. Drives the bar on the notice.</summary>
    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial int DownloadPercent { get; set; }

    /// <summary>The furthest the bar reached, kept for the tests to read.</summary>
    internal int HighestPercentSeen { get; private set; }

    public string NoticeTitle => IsDownloading ? "Downloading the update" : "Update is available";

    /// <summary>
    /// The two version numbers and nothing else. What the buttons underneath do
    /// says the rest, and what changed is in the release notes.
    /// </summary>
    public string NoticeDetail =>
        IsDownloading
            ? $"{InstalledVersion} → {LatestVersion}   {DownloadPercent}%"
            : LatestVersion.Length > 0 ? $"{InstalledVersion} → {LatestVersion}" : string.Empty;

    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnUpdateReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNotice));
        OnPropertyChanged(nameof(CanDownload));
    }

    partial void OnLatestVersionChanged(string value) => OnPropertyChanged(nameof(NoticeDetail));

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(NoticeTitle));
        OnPropertyChanged(nameof(NoticeDetail));
        OnPropertyChanged(nameof(ShowNotice));
    }

    partial void OnDownloadPercentChanged(int value)
    {
        if (value > HighestPercentSeen)
        {
            HighestPercentSeen = value;
        }

        OnPropertyChanged(nameof(NoticeDetail));
    }

    partial void OnUpdateFoundChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ShowNotice));
    }

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
            LatestVersion = ready.LatestVersion;
            LatestLabel = Describe(ready.LatestVersion, ready.LatestCommit);
            CheckStatus = ready.Detail;
        }
    }

    /// <summary>
    /// Asks GitHub once for this start, then keeps an eye on the service if one
    /// is registered. Started without being awaited, because a start must not
    /// wait on the network to put a window up.
    /// </summary>
    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        await CheckOnStartAsync(cancellationToken).ConfigureAwait(true);

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

    /// <summary>
    /// The check a start makes on its own. It reports nothing when it does not
    /// finish: a machine that opened the application on a train has no use for a
    /// banner saying GitHub could not be reached, and the Check for updates
    /// button is there to say so on request.
    /// </summary>
    private async Task CheckOnStartAsync(CancellationToken cancellationToken)
    {
        if (IsWorking || UpdateReady)
        {
            return;
        }

        _log.Write("Asking GitHub for the latest release, because the application has just started.");

        try
        {
            var status = await _host.CheckAsync(cancellationToken).ConfigureAwait(true);

            _log.Write($"The start check answered {status.Outcome}. {status.Detail}");

            if (status.Outcome == UpdateOutcome.CheckFailed)
            {
                return;
            }

            if (status.LatestVersion.Length > 0)
            {
                LatestVersion = status.LatestVersion;
                LatestLabel = Describe(status.LatestVersion, status.LatestCommit);
            }

            UpdateReady = status.Ready;
            UpdateFound = status.Outcome == UpdateOutcome.UpdateAvailable && !status.Ready;

            if (UpdateFound || UpdateReady)
            {
                CheckStatus = "An update is available. " + status.Detail;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reported to the log and nowhere else. A start with no connection
            // has no use for a banner, but a check that fails every time and
            // leaves no trace cannot be diagnosed on someone else's machine.
            _log.Write("The check this start made did not finish", ex);
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

            LatestVersion = status.LatestVersion;
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
            UpdateFound = status.Outcome == UpdateOutcome.UpdateAvailable && !status.Ready;
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

    /// <summary>
    /// Takes the release the check found. The service does this on its own when
    /// it is registered; with automatic updates off this is the only way to get
    /// a build from inside the application.
    /// </summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsWorking)
        {
            return;
        }

        IsWorking = true;
        IsDownloading = true;
        DownloadPercent = 0;
        CheckStatus = "Downloading the release and checking it against its published checksum.";

        try
        {
            var progress = new Progress<double>(fraction =>
                DownloadPercent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100));

            var status = await _host.FetchAsync(CancellationToken.None, progress).ConfigureAwait(true);

            CheckStatus = status.Detail;
            UpdateReady = status.Ready;
            UpdateFound = status.Outcome == UpdateOutcome.UpdateAvailable && !status.Ready;

            if (status.LatestVersion.Length > 0)
            {
                LatestVersion = status.LatestVersion;
                LatestLabel = Describe(status.LatestVersion, status.LatestCommit);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CheckStatus = "The download did not finish. " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
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
