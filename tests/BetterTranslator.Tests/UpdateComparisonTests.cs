using System.IO;
using System.Net.Http;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates.Releases;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class UpdateComparisonTests
{
    private static BuildIdentity Installed(string version, string commit) =>
        new(version, commit, "stable", DateTimeOffset.UnixEpoch);

    private static ReleaseInfo Release(string tag, string commit) =>
        new(tag, commit, DateTimeOffset.UnixEpoch, []);

    [Fact]
    public void A_higher_release_version_is_an_update()
    {
        var verdict = UpdateComparer.Compare(
            Installed("1.0.0", "a54ff15"),
            Release("v1.0.1", "b7d41c9"));

        verdict.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        verdict.LatestVersion.Should().Be("1.0.1");
    }

    [Fact]
    public void The_same_version_from_a_different_commit_is_an_update()
    {
        var verdict = UpdateComparer.Compare(
            Installed("1.0.0", "a54ff15"),
            Release("v1.0.0", "f9457db"));

        verdict.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        verdict.Detail.Should().Contain("f9457db");
    }

    [Fact]
    public void The_same_version_from_the_same_commit_is_up_to_date()
    {
        var verdict = UpdateComparer.Compare(
            Installed("1.0.0", "a54ff15"),
            Release("v1.0.0", "a54ff15"));

        verdict.Outcome.Should().Be(UpdateOutcome.UpToDate);
    }

    [Fact]
    public void A_short_commit_matches_the_long_one_it_was_cut_from()
    {
        var verdict = UpdateComparer.Compare(
            Installed("1.0.0", "f9457db"),
            Release("v1.0.0", "f9457dbb1c2d3e4f5061728394a5b6c7d8e9f0a1"));

        verdict.Outcome.Should().Be(UpdateOutcome.UpToDate);
    }

    [Fact]
    public void An_unknown_commit_on_either_side_leaves_the_versions_to_decide()
    {
        UpdateComparer.Compare(Installed("1.0.0", string.Empty), Release("v1.0.0", "f9457db"))
            .Outcome.Should().Be(UpdateOutcome.UpToDate);

        UpdateComparer.Compare(Installed("1.0.0", "a54ff15"), Release("v1.0.0", string.Empty))
            .Outcome.Should().Be(UpdateOutcome.UpToDate);
    }

    [Fact]
    public void The_placeholder_commit_counts_as_no_commit()
    {
        var identity = Installed("1.0.0", BuildIdentity.UnknownCommit);

        identity.HasCommit.Should().BeFalse();

        UpdateComparer.Compare(identity, Release("v1.0.0", "f9457db"))
            .Outcome.Should().Be(UpdateOutcome.UpToDate);
    }

    [Fact]
    public void A_lower_release_version_is_never_an_update()
    {
        var verdict = UpdateComparer.Compare(
            Installed("1.2.0", "a54ff15"),
            Release("v1.1.9", "f9457db"));

        verdict.Outcome.Should().Be(UpdateOutcome.UpToDate);
        verdict.Detail.Should().Contain("older");
    }

    [Fact]
    public void A_prerelease_ranks_below_the_release_it_leads_to()
    {
        UpdateComparer.Compare(Installed("1.1.0-beta.1", "a54ff15"), Release("v1.1.0", "f9457db"))
            .Outcome.Should().Be(UpdateOutcome.UpdateAvailable);

        UpdateComparer.Compare(Installed("1.1.0", "a54ff15"), Release("v1.1.0-beta.2", "f9457db"))
            .Outcome.Should().Be(UpdateOutcome.UpToDate);
    }

    [Fact]
    public void Two_prereleases_of_one_version_order_by_their_identifiers()
    {
        UpdateComparer.Compare(Installed("1.1.0-beta.2", "a54ff15"), Release("v1.1.0-beta.10", "f9457db"))
            .Outcome.Should().Be(UpdateOutcome.UpdateAvailable);

        UpdateComparer.Compare(Installed("1.1.0-beta.10", "a54ff15"), Release("v1.1.0-beta.2", "f9457db"))
            .Outcome.Should().Be(UpdateOutcome.UpToDate);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("1.0.0.4")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    [InlineData("")]
    public void A_tag_that_is_not_a_version_fails_the_check(string tag)
    {
        var verdict = UpdateComparer.Compare(Installed("1.0.0", "a54ff15"), Release(tag, "f9457db"));

        verdict.Outcome.Should().Be(UpdateOutcome.CheckFailed);
        verdict.InstalledVersion.Should().Be("1.0.0");
    }

    [Fact]
    public void A_lookup_that_failed_is_reported_as_a_failed_check()
    {
        var verdict = UpdateComparer.Compare(
            Installed("1.0.0", "a54ff15"),
            ReleaseLookup.Failed("GitHub answered 500 Internal Server Error."));

        verdict.Outcome.Should().Be(UpdateOutcome.CheckFailed);
        verdict.Detail.Should().Contain("500");
        verdict.LatestVersion.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("1.2.3+abc1234", 1, 2, 3, "")]
    public void Versions_parse_the_way_semver_says(string text, int major, int minor, int patch, string preRelease)
    {
        SemanticVersion.TryParse(text, out var version).Should().BeTrue();

        version.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
        version.PreRelease.Should().Be(preRelease);
    }

    [Fact]
    public void Version_ordering_holds_across_the_parts()
    {
        SemanticVersion.Parse("2.0.0").Should().BeGreaterThan(SemanticVersion.Parse("1.9.9"));
        SemanticVersion.Parse("1.10.0").Should().BeGreaterThan(SemanticVersion.Parse("1.9.0"));
        SemanticVersion.Parse("1.0.1").Should().BeGreaterThan(SemanticVersion.Parse("1.0.0"));
        SemanticVersion.Parse("1.0.0").Should().BeGreaterThan(SemanticVersion.Parse("1.0.0-rc.1"));
        SemanticVersion.Parse("1.0.0-rc.2").Should().BeGreaterThan(SemanticVersion.Parse("1.0.0-rc.1"));
        SemanticVersion.Parse("1.0.0-rc.1").Should().Be(SemanticVersion.Parse("1.0.0-rc.1"));
    }
}

/// <summary>
/// A release can be tagged at a commit no published binary was built from. The
/// version and the commit then disagree for ever, and an updater that trusts
/// them alone offers the same release, installs it, and offers it again. The
/// published checksum is what settles it.
/// </summary>
public sealed class UpdateConvergenceTests
{
    private static readonly byte[] Payload = [7, 7, 7, 7];

    private static string PayloadSha =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Payload)).ToLowerInvariant();

    private static ReleaseInfo Release(string tag, string commit, string digest, long size) =>
        new(tag, commit, DateTimeOffset.UnixEpoch, [
            new ReleaseAsset(UpdatePaths.PayloadAssetName, size, "https://github.com/x/y/releases/download/z/BetterTranslator.exe", digest),
        ]);

    [Fact]
    public async Task A_release_whose_bytes_are_already_installed_is_not_offered_again()
    {
        using var root = new TemporaryUpdateRoot();

        // The build recorded as installed carries exactly the published bytes,
        // while the tag names a commit the build never came from.
        string installed = Path.Combine(root.Root, UpdatePaths.PayloadAssetName);
        await File.WriteAllBytesAsync(installed, Payload);
        new InstalledAppStore(root.Paths).Write(installed, "1.0.4", "fdc0818");

        var release = Release("v1.0.4", "349d968aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PayloadSha, Payload.Length);
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));

        using var http = new HttpClient(new RecordingHandler(_ => RecordingHandler.Text(string.Empty)));

        var workflow = new UpdateWorkflow(
            client,
            http,
            root.Paths,
            null,
            new BuildIdentity("1.0.4", "fdc0818", "stable", DateTimeOffset.UnixEpoch));

        var status = await workflow.CheckAsync(CancellationToken.None);

        status.Outcome.Should().Be(
            UpdateOutcome.UpToDate,
            "the bytes on disk are the bytes the release publishes, whatever the commits say");
        status.Detail.Should().Contain("already installed");
    }

    [Fact]
    public async Task A_release_whose_bytes_differ_at_the_same_version_is_still_offered()
    {
        using var root = new TemporaryUpdateRoot();

        string installed = Path.Combine(root.Root, UpdatePaths.PayloadAssetName);
        await File.WriteAllBytesAsync(installed, [1, 2, 3, 4]);
        new InstalledAppStore(root.Paths).Write(installed, "1.0.4", "fdc0818");

        var release = Release("v1.0.4", "349d968aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PayloadSha, Payload.Length);
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));

        using var http = new HttpClient(new RecordingHandler(_ => RecordingHandler.Text(string.Empty)));

        var workflow = new UpdateWorkflow(
            client,
            http,
            root.Paths,
            null,
            new BuildIdentity("1.0.4", "fdc0818", "stable", DateTimeOffset.UnixEpoch));

        var status = await workflow.CheckAsync(CancellationToken.None);

        status.Outcome.Should().Be(
            UpdateOutcome.UpdateAvailable,
            "a genuinely different build at the same version is still worth taking");
    }

    [Fact]
    public async Task A_newer_version_is_offered_without_reading_the_installed_file()
    {
        using var root = new TemporaryUpdateRoot();

        var release = Release("v1.0.5", "349d968aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PayloadSha, Payload.Length);
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));

        using var http = new HttpClient(new RecordingHandler(_ => RecordingHandler.Text(string.Empty)));

        var workflow = new UpdateWorkflow(
            client,
            http,
            root.Paths,
            null,
            new BuildIdentity("1.0.4", "fdc0818", "stable", DateTimeOffset.UnixEpoch));

        var status = await workflow.CheckAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.UpdateAvailable);
        status.LatestVersion.Should().Be("1.0.5");
    }
}
