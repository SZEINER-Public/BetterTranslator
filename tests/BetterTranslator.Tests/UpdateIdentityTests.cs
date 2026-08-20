using BetterTranslator.App.Services;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Releases;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The service maintains one recorded executable and answers about that one. The
/// application asking it may be a different copy: a build run from somewhere
/// else, or the one a half finished install left recorded. An answer about the
/// wrong build is worse than no answer, because it says up to date and hides the
/// update, so what the service found is judged again against the build asking.
/// </summary>
public sealed class UpdateIdentityTests
{
    private static readonly BuildIdentity Running =
        new("1.0.3", "349d968", "stable", DateTimeOffset.UnixEpoch);

    private static UpdateStatus FromService(
        UpdateOutcome outcome,
        string detail,
        string installedVersion,
        string latestVersion,
        bool ready = false) =>
        new(outcome, detail, installedVersion, "abcdef1", latestVersion, "b71cc02", ready, DateTimeOffset.UnixEpoch);

    [Fact]
    public void An_answer_about_another_build_is_judged_again_against_this_one()
    {
        var answered = FromService(
            UpdateOutcome.UpToDate,
            "1.0.4 is the latest release.",
            installedVersion: "1.0.4",
            latestVersion: "1.0.4");

        var mine = UpdaterGateway.ForBuild(Running, answered);

        mine.Outcome.Should().Be(
            UpdateOutcome.UpdateAvailable,
            "the service keeps a 1.0.4 executable, but the build asking is 1.0.3");
        mine.InstalledVersion.Should().Be("1.0.3");
        mine.LatestVersion.Should().Be("1.0.4");
    }

    [Fact]
    public void An_answer_about_this_build_is_left_alone()
    {
        var answered = FromService(
            UpdateOutcome.UpdateAvailable,
            "1.0.4 is newer than the installed 1.0.3.",
            installedVersion: "1.0.3",
            latestVersion: "1.0.4");

        var mine = UpdaterGateway.ForBuild(Running, answered);

        mine.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        mine.InstalledVersion.Should().Be("1.0.3");
        mine.LatestVersion.Should().Be("1.0.4");
    }

    [Fact]
    public void A_release_older_than_this_build_is_not_offered()
    {
        var answered = FromService(
            UpdateOutcome.UpdateAvailable,
            "1.0.2 is newer than the installed 1.0.1.",
            installedVersion: "1.0.1",
            latestVersion: "1.0.2");

        var mine = UpdaterGateway.ForBuild(Running, answered);

        mine.Outcome.Should().Be(UpdateOutcome.UpToDate, "1.0.2 is behind the 1.0.3 that is running");
    }

    [Fact]
    public void A_check_that_did_not_finish_is_passed_through()
    {
        var answered = FromService(
            UpdateOutcome.CheckFailed,
            "GitHub could not be reached.",
            installedVersion: "1.0.4",
            latestVersion: string.Empty);

        var mine = UpdaterGateway.ForBuild(Running, answered);

        mine.Outcome.Should().Be(UpdateOutcome.CheckFailed);
        mine.Detail.Should().Contain("could not be reached");
    }

    [Fact]
    public void A_payload_the_service_already_staged_stays_reported_as_ready()
    {
        var answered = FromService(
            UpdateOutcome.UpToDate,
            "1.0.4 is the latest release.",
            installedVersion: "1.0.4",
            latestVersion: "1.0.4",
            ready: true);

        var mine = UpdaterGateway.ForBuild(Running, answered);

        mine.Ready.Should().BeTrue("the verified payload is on disk whoever it was fetched for");
        mine.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
    }
}

/// <summary>
/// The service fetches and installs for the one executable it was registered
/// against. Handing it a download meant for a different copy asks it to do work
/// it will correctly decline, and the button appears to do nothing at all.
/// </summary>
public sealed class UpdateFetchRoutingTests
{
    [Fact]
    public void The_service_is_asked_only_when_it_maintains_this_very_executable()
    {
        UpdaterGateway.Maintains(
                @"C:\Program Files\BetterTranslator\BetterTranslator.exe",
                @"C:\Program Files\BetterTranslator\BetterTranslator.exe")
            .Should().BeTrue();
    }

    [Fact]
    public void A_path_that_differs_only_in_case_or_shape_is_the_same_executable()
    {
        UpdaterGateway.Maintains(
                @"C:\Program Files\BetterTranslator\BetterTranslator.exe",
                @"c:\program files\BetterTranslator\.\BetterTranslator.exe")
            .Should().BeTrue();
    }

    [Fact]
    public void A_copy_running_from_somewhere_else_fetches_for_itself()
    {
        UpdaterGateway.Maintains(
                @"H:\build\bin\Release\BetterTranslator.exe",
                @"C:\Users\someone\Programs\BetterTranslator\BetterTranslator.exe")
            .Should().BeFalse(
                "the service would fetch for the build it keeps, which leaves this one exactly where it was");
    }

    [Theory]
    [InlineData(null, @"C:\x\BetterTranslator.exe")]
    [InlineData(@"C:\x\BetterTranslator.exe", null)]
    [InlineData("", "")]
    public void Nothing_recorded_or_nothing_running_is_not_a_match(string? recorded, string? running)
    {
        UpdaterGateway.Maintains(recorded, running).Should().BeFalse();
    }
}
