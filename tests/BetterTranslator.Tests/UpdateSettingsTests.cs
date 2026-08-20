using System.Net.Http;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Releases;
using BetterTranslator.Updates.Service;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

internal sealed class FakeUpdaterHost : IUpdaterHost
{
    public BuildIdentity Installed { get; set; } =
        new("1.0.0", "a54ff15", "stable", DateTimeOffset.UnixEpoch);

    public ServiceState Current { get; set; } = ServiceState.NotInstalled;

    public ServiceState AfterEnable { get; set; } = ServiceState.Running;

    public ServiceState AfterDisable { get; set; } = ServiceState.NotInstalled;

    public ElevationOutcome EnableOutcome { get; set; } = new(true, string.Empty);

    public ElevationOutcome DisableOutcome { get; set; } = new(true, string.Empty);

    public UpdateStatus Answer { get; set; } = new(
        UpdateOutcome.UpToDate,
        "1.0.0+a54ff15 is the latest build.",
        "1.0.0",
        "a54ff15",
        "1.0.0",
        "a54ff15",
        Ready: false,
        DateTimeOffset.UnixEpoch);

    public UpdateStatus? Staged { get; set; }

    /// <summary>What the fetch answers. Falls back to <see cref="Answer"/>.</summary>
    public UpdateStatus? FetchAnswer { get; set; }

    public Exception? CheckThrows { get; set; }

    public Exception? FetchThrows { get; set; }

    public int Checks { get; private set; }

    public int Fetches { get; private set; }

    public int Enables { get; private set; }

    public int Disables { get; private set; }

    public ServiceState State() => Current;

    public Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;

        return CheckThrows is not null ? Task.FromException<UpdateStatus>(CheckThrows) : Task.FromResult(Answer);
    }

    public Task<UpdateStatus> FetchAsync(CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        progress?.Report(0.5);

        Fetches++;

        return FetchThrows is not null
            ? Task.FromException<UpdateStatus>(FetchThrows)
            : Task.FromResult(FetchAnswer ?? Answer);
    }

    public Task<UpdateStatus?> ReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Staged);

    public Task<ElevationOutcome> EnableAsync(CancellationToken cancellationToken)
    {
        Enables++;

        if (EnableOutcome.Ok)
        {
            Current = AfterEnable;
        }

        return Task.FromResult(EnableOutcome);
    }

    public Task<ElevationOutcome> DisableAsync(CancellationToken cancellationToken)
    {
        Disables++;

        if (DisableOutcome.Ok)
        {
            Current = AfterDisable;
        }

        return Task.FromResult(DisableOutcome);
    }

    public Task<ElevationOutcome> HandOverToStagedBuildAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ElevationOutcome(false, "Nothing is staged to install."));
}

public sealed class UpdateSettingsTests
{
    [Fact]
    public void A_service_that_was_never_registered_reads_as_not_installed()
    {
        ServiceControl.Query("BetterTranslatorUpdaterThatWasNeverRegistered")
            .Should().Be(ServiceState.NotInstalled);
    }

    [Fact]
    public async Task The_toggle_shows_what_the_service_manager_says_rather_than_a_stored_flag()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.Running };
        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.AutomaticUpdates.Should().BeTrue();
        updates.ServiceStatus.Should().Contain("running");

        host.Current = ServiceState.NotInstalled;

        var fresh = new UpdatesViewModel(host);
        await fresh.LoadAsync(CancellationToken.None);

        fresh.AutomaticUpdates.Should().BeFalse();
        fresh.ServiceStatus.Should().Contain("No updater is registered");
        host.Enables.Should().Be(0, "loading the screen registers nothing");
    }

    [Fact]
    public async Task A_stopped_service_still_counts_as_on()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.Stopped };
        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.AutomaticUpdates.Should().BeTrue();
        updates.ServiceStatus.Should().Contain("not running");
    }

    [Fact]
    public async Task Switching_it_on_registers_the_service_once()
    {
        var host = new FakeUpdaterHost();
        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.AutomaticUpdates = true;
        await updates.Pending;

        host.Enables.Should().Be(1);
        updates.AutomaticUpdates.Should().BeTrue();
        updates.ServiceStatus.Should().Contain("running");
    }

    [Fact]
    public async Task A_refused_prompt_puts_the_toggle_back_where_it_was()
    {
        var host = new FakeUpdaterHost
        {
            EnableOutcome = new ElevationOutcome(false, "The administrator prompt was refused, so nothing changed."),
        };

        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.AutomaticUpdates = true;
        await updates.Pending;

        host.Enables.Should().Be(1);
        updates.AutomaticUpdates.Should().BeFalse();
        updates.ServiceStatus.Should().Contain("refused");
    }

    [Fact]
    public async Task Switching_it_off_removes_the_service()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.Running };
        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.AutomaticUpdates = false;
        await updates.Pending;

        host.Disables.Should().Be(1);
        updates.AutomaticUpdates.Should().BeFalse();
        updates.ServiceStatus.Should().Contain("No updater is registered");
    }

    [Fact]
    public async Task The_check_button_reaches_a_terminal_answer_with_no_service_on_the_machine()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.NotInstalled };
        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);
        await updates.CheckCommand.ExecuteAsync(null);

        host.Checks.Should().Be(1);
        updates.IsWorking.Should().BeFalse();
        updates.CheckStatus.Should().StartWith("This is the latest build.");
        updates.LatestLabel.Should().Be("1.0.0+a54ff15");
    }

    [Fact]
    public async Task An_available_update_is_named_with_both_versions()
    {
        var host = new FakeUpdaterHost
        {
            Answer = new UpdateStatus(
                UpdateOutcome.UpdateAvailable,
                "1.0.1 is newer than 1.0.0.",
                "1.0.0",
                "a54ff15",
                "1.0.1",
                "b7d41c9e2f3a5061728394a5b6c7d8e9f0a1b2c3",
                Ready: false,
                DateTimeOffset.UnixEpoch),
        };

        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);

        updates.InstalledLabel.Should().Be("1.0.0+a54ff15");
        updates.LatestLabel.Should().Be("1.0.1+b7d41c9");
        updates.CheckStatus.Should().StartWith("An update is available.");
    }

    [Fact]
    public async Task A_check_that_fails_says_so_and_stops_working()
    {
        var host = new FakeUpdaterHost
        {
            Answer = new UpdateStatus(
                UpdateOutcome.CheckFailed,
                "Could not reach GitHub.",
                "1.0.0",
                "a54ff15",
                string.Empty,
                string.Empty,
                Ready: false,
                DateTimeOffset.UnixEpoch),
        };

        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);

        updates.IsWorking.Should().BeFalse();
        updates.CheckStatus.Should().StartWith("The check did not finish.");
        updates.LatestLabel.Should().Be("The latest release could not be read.");
    }

    [Fact]
    public async Task A_check_that_throws_still_leaves_a_terminal_state()
    {
        var host = new FakeUpdaterHost { CheckThrows = new InvalidOperationException("the pipe broke") };
        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);

        updates.IsWorking.Should().BeFalse();
        updates.CanInteract.Should().BeTrue();
        updates.CheckStatus.Should().Contain("the pipe broke");
    }

    [Fact]
    public async Task A_staged_build_raises_the_restart_offer_until_it_is_dismissed()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.Running,
            Staged = new UpdateStatus(
                UpdateOutcome.UpdateAvailable,
                "1.0.1 is downloaded and verified.",
                "1.0.0",
                "a54ff15",
                "1.0.1",
                "b7d41c9",
                Ready: true,
                DateTimeOffset.UnixEpoch),
        };

        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.UpdateReady.Should().BeTrue();
        updates.ShowNotice.Should().BeTrue();

        updates.DismissCommand.Execute(null);

        updates.ShowNotice.Should().BeFalse();
        updates.UpdateReady.Should().BeTrue();
    }

    [Fact]
    public async Task A_build_with_no_recorded_commit_says_so_rather_than_showing_a_placeholder()
    {
        var host = new FakeUpdaterHost
        {
            Installed = new BuildIdentity("1.0.0", BuildIdentity.UnknownCommit, "stable", DateTimeOffset.UnixEpoch),
        };

        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        updates.InstalledLabel.Should().Be("1.0.0, built from an unrecorded commit");
    }

    [Fact]
    public void The_settings_screen_carries_the_updates_tab()
    {
        var updates = new UpdatesViewModel(new FakeUpdaterHost());

        var settings = new SettingsViewModel(
            store: null!,
            cache: null!,
            paths: null!,
            applied: _ => Task.CompletedTask,
            close: () => { },
            storedDataDeleted: () => Task.CompletedTask,
            openDownloads: () => { })
        {
            Updates = updates,
        };

        settings.IsUpdatesTab.Should().BeFalse();

        settings.ShowUpdatesCommand.Execute(null);

        settings.Tab.Should().Be(SettingsTab.Updates);
        settings.IsUpdatesTab.Should().BeTrue();
        settings.Updates.Should().BeSameAs(updates);
    }

    [Fact]
    public void The_installed_build_identity_is_read_from_the_stamped_assembly()
    {
        var identity = BuildIdentity.Current;

        identity.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        identity.Channel.Should().NotBeEmpty();
        identity.Display.Should().StartWith(identity.Version);

        BuildIdentity.Read(typeof(UpdatePaths).Assembly).Version.Should().Be(identity.Version);
    }
}

public sealed class UpdateDownloadTests
{
    private static UpdateStatus Available(bool ready = false) => new(
        UpdateOutcome.UpdateAvailable,
        "1.0.3 is newer than the installed 1.0.0.",
        "1.0.0",
        "a54ff15",
        "1.0.3",
        "b71cc02",
        Ready: ready,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_check_that_finds_an_update_offers_to_fetch_it()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.NotInstalled, Answer = Available() };
        var updates = new UpdatesViewModel(host);

        updates.CanDownload.Should().BeFalse("nothing has been asked for yet");

        await updates.CheckCommand.ExecuteAsync(null);

        updates.CanDownload.Should().BeTrue(
            "with no service to fetch it, this screen is the only thing that can, and saying an "
            + "update exists while offering no way to take it is a dead end");
        updates.UpdateReady.Should().BeFalse("nothing has been downloaded yet");
        host.Fetches.Should().Be(0, "the check asks GitHub and stops there");
    }

    [Fact]
    public async Task A_check_that_finds_nothing_offers_no_download()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.NotInstalled };
        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);

        updates.CanDownload.Should().BeFalse("this is the latest build");
    }

    [Fact]
    public async Task Downloading_stages_the_build_and_hands_over_to_the_restart_card()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Answer = Available(),
            FetchAnswer = Available(ready: true) with { Detail = "1.0.3 is staged and verified." },
        };

        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);
        await updates.DownloadCommand.ExecuteAsync(null);

        host.Fetches.Should().Be(1);
        updates.UpdateReady.Should().BeTrue("a verified payload is waiting");
        updates.CanDownload.Should().BeFalse("it is downloaded; the restart card is the next step");
        updates.CheckStatus.Should().Contain("staged");
    }

    [Fact]
    public async Task A_download_that_fails_says_so_and_stays_offered()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Answer = Available(),
            FetchThrows = new HttpRequestException("the connection was reset"),
        };

        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);
        await updates.DownloadCommand.ExecuteAsync(null);

        updates.UpdateReady.Should().BeFalse();
        updates.CanDownload.Should().BeTrue("a failed download is worth another try");
        updates.CheckStatus.Should().Contain("the connection was reset");
    }
}

public sealed class UpdateStartupCheckTests
{
    private static UpdateStatus Available() => new(
        UpdateOutcome.UpdateAvailable,
        "1.0.3 is newer than the installed 1.0.0.",
        "1.0.0",
        "a54ff15",
        "1.0.3",
        "b71cc02",
        Ready: false,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_start_asks_github_and_surfaces_what_it_finds()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.NotInstalled, Answer = Available() };
        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);

        host.Checks.Should().Be(0, "loading the screen reads local state and nothing else");

        await updates.WatchAsync(CancellationToken.None);

        host.Checks.Should().Be(1, "a start asks once");
        host.Fetches.Should().Be(0, "nothing is downloaded until someone asks for it");
        updates.CanDownload.Should().BeTrue();
        updates.ShowNotice.Should().BeTrue("a check nobody is shown is no use");
        updates.NoticeTitle.Should().Be("Update is available");
        updates.NoticeDetail.Should().Be("1.0.0 → 1.0.3", "the notice carries the two versions and nothing else");
    }

    [Fact]
    public async Task A_start_with_no_connection_says_nothing()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            CheckThrows = new HttpRequestException("no such host is known"),
        };

        var updates = new UpdatesViewModel(host);
        string before = updates.CheckStatus;

        await updates.WatchAsync(CancellationToken.None);

        updates.CheckStatus.Should().Be(before, "starting without a connection is not a fault to report");
        updates.ShowNotice.Should().BeFalse();
        updates.CanDownload.Should().BeFalse();
    }

    [Fact]
    public async Task A_start_where_the_check_does_not_finish_says_nothing()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Answer = new UpdateStatus(
                UpdateOutcome.CheckFailed,
                "GitHub answered 403 because its rate limit is spent.",
                "1.0.0",
                "a54ff15",
                string.Empty,
                string.Empty,
                Ready: false,
                DateTimeOffset.UnixEpoch),
        };

        var updates = new UpdatesViewModel(host);
        string before = updates.CheckStatus;

        await updates.WatchAsync(CancellationToken.None);

        updates.CheckStatus.Should().Be(before, "a spent rate limit at startup is not worth a banner");
        updates.ShowNotice.Should().BeFalse();
    }

    [Fact]
    public async Task A_start_on_the_latest_build_shows_no_notice()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.NotInstalled };
        var updates = new UpdatesViewModel(host);

        await updates.WatchAsync(CancellationToken.None);

        host.Checks.Should().Be(1);
        updates.ShowNotice.Should().BeFalse();
        updates.CanDownload.Should().BeFalse();
    }

    [Fact]
    public async Task A_build_already_downloaded_is_not_checked_for_again()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Staged = Available() with { Ready = true, Detail = "1.0.3 is downloaded and verified." },
        };

        var updates = new UpdatesViewModel(host);

        await updates.LoadAsync(CancellationToken.None);
        await updates.WatchAsync(CancellationToken.None);

        host.Checks.Should().Be(0, "there is already a verified build waiting; asking again changes nothing");
        updates.ShowNotice.Should().BeTrue();
        updates.NoticeTitle.Should().Be("Update is available");
        updates.NoticeDetail.Should().Be("1.0.0 → 1.0.3");
    }
}

/// <summary>
/// A payload of well over a hundred megabytes arrives over half a minute or
/// more. Without something moving on screen the button simply disappears and
/// the window sits there, which reads as a press that did nothing.
/// </summary>
public sealed class UpdateProgressTests
{
    private static UpdateStatus Available(bool ready = false) => new(
        UpdateOutcome.UpdateAvailable,
        "1.0.6 is newer than the installed 1.0.5.",
        "1.0.5",
        "a54ff15",
        "1.0.6",
        "b71cc02",
        Ready: ready,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Nothing_is_downloading_before_the_button_is_pressed()
    {
        var host = new FakeUpdaterHost { Current = ServiceState.NotInstalled, Answer = Available() };
        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);

        updates.IsDownloading.Should().BeFalse();
        updates.DownloadPercent.Should().Be(0);
    }

    /// <summary>
    /// Progress is handed over through <see cref="Progress{T}"/>, which posts to
    /// the context it was built on. The window has one and the callback lands on
    /// the interface thread; a test has none and the callback would go to the
    /// thread pool and arrive after the assertion. This gives it one that runs
    /// the callback where it is raised.
    /// </summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    [Fact]
    public async Task A_download_reports_how_far_along_it_is()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Answer = Available(),
            FetchAnswer = Available(ready: true) with { Detail = "1.0.6 is staged and verified." },
        };

        var restore = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineContext());

        try
        {
            var updates = new UpdatesViewModel(host);

            await updates.CheckCommand.ExecuteAsync(null);
            await updates.DownloadCommand.ExecuteAsync(null);

            // The fake reports half way through before answering.
            updates.HighestPercentSeen.Should().Be(50, "the bar has to move while the bytes arrive");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(restore);
        }
    }

    [Fact]
    public async Task A_finished_download_stops_reporting_progress()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Answer = Available(),
            FetchAnswer = Available(ready: true) with { Detail = "1.0.6 is staged and verified." },
        };

        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);
        await updates.DownloadCommand.ExecuteAsync(null);

        updates.IsDownloading.Should().BeFalse("the payload is on disk; the next step is the restart");
        updates.UpdateReady.Should().BeTrue();
    }

    [Fact]
    public async Task A_download_that_fails_stops_reporting_progress_too()
    {
        var host = new FakeUpdaterHost
        {
            Current = ServiceState.NotInstalled,
            Answer = Available(),
            FetchThrows = new HttpRequestException("the connection was reset"),
        };

        var updates = new UpdatesViewModel(host);

        await updates.CheckCommand.ExecuteAsync(null);
        await updates.DownloadCommand.ExecuteAsync(null);

        updates.IsDownloading.Should().BeFalse();
        updates.CanDownload.Should().BeTrue("a failed download is worth another try");
    }
}
