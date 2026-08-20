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
