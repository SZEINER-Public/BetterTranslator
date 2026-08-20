using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Payload;
using BetterTranslator.Updates.Releases;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class UpdatePayloadTests
{
    private const string InstalledBody = "the build that is installed";

    private static readonly byte[] NewBuild = Encoding.UTF8.GetBytes("the build that was published");

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BuildIdentity Identity(string version) =>
        new(version, "a54ff15", "stable", DateTimeOffset.UnixEpoch);

    private static ReleaseInfo Published(string tag, string commit, string? digest, bool withChecksums = false)
    {
        var assets = new List<ReleaseAsset>
        {
            new("BetterTranslator.exe", NewBuild.Length, "https://example.test/BetterTranslator.exe", digest),
        };

        if (withChecksums)
        {
            assets.Add(new ReleaseAsset("checksums.txt", 128, "https://example.test/checksums.txt", null));
        }

        return new ReleaseInfo(tag, commit, DateTimeOffset.UnixEpoch, assets);
    }

    private static string Install(TemporaryUpdateRoot root)
    {
        var folder = Path.Combine(root.Root, "app");
        Directory.CreateDirectory(folder);

        var executable = Path.Combine(folder, "BetterTranslator.exe");
        File.WriteAllText(executable, InstalledBody);

        new InstalledAppStore(root.Paths).Write(executable, Identity("1.0.0"));

        return executable;
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_published_checksum_is_refused_and_deleted()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        var handler = new RecordingHandler(_ => RecordingHandler.Bytes(NewBuild));
        using var http = new HttpClient(handler);

        var release = Published("v1.0.1", "b7d41c9", new string('a', 64));
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.CheckFailed);
        status.Detail.Should().Contain("checksum");

        File.Exists(root.Paths.StagedPayload).Should().BeFalse();
        File.Exists(root.Paths.PartialPayload).Should().BeFalse();
        File.ReadAllText(installed).Should().Be(InstalledBody);
    }

    [Fact]
    public async Task A_release_that_publishes_no_checksum_is_refused_without_downloading()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        var handler = new RecordingHandler(_ => RecordingHandler.Bytes(NewBuild));
        using var http = new HttpClient(handler);

        var client = new RecordedReleaseClient(ReleaseLookup.Found(Published("v1.0.1", "b7d41c9", null)));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.CheckFailed);
        status.Detail.Should().Contain("SHA-256");

        handler.Exchanges.Should().BeEmpty();
        File.Exists(root.Paths.StagedPayload).Should().BeFalse();
        File.ReadAllText(installed).Should().Be(InstalledBody);
    }

    [Fact]
    public async Task A_verified_download_replaces_the_installed_build_and_keeps_the_old_one_aside()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        var handler = new RecordingHandler(_ => RecordingHandler.Bytes(NewBuild));
        using var http = new HttpClient(handler);

        var release = Published("v1.0.1", "b7d41c9", Sha256Of(NewBuild));
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.UpToDate);
        File.ReadAllBytes(installed).Should().Equal(NewBuild);

        var folder = Path.GetDirectoryName(installed)!;

        Directory.GetFiles(folder, "BetterTranslator.exe.superseded-*").Should().ContainSingle();

        workflow.RemoveSupersededBuilds(installed);

        Directory.GetFiles(folder, "BetterTranslator.exe.superseded-*").Should().BeEmpty();
    }

    [Fact]
    public async Task The_checksum_may_come_from_a_checksums_asset_when_the_api_publishes_no_digest()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        var checksums = $"{Sha256Of(NewBuild)}  BetterTranslator.exe\n" + new string('b', 64) + "  notes.txt\n";

        var handler = new RecordingHandler(
            request => request.RequestUri!.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal)
                ? RecordingHandler.Text(checksums)
                : RecordingHandler.Bytes(NewBuild));

        using var http = new HttpClient(handler);

        var release = Published("v1.0.1", "b7d41c9", null, withChecksums: true);
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.UpToDate);
        File.ReadAllBytes(installed).Should().Equal(NewBuild);
    }

    [Fact]
    public async Task An_older_release_is_never_downloaded_and_never_applied()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        var handler = new RecordingHandler(_ => RecordingHandler.Bytes(NewBuild));
        using var http = new HttpClient(handler);

        var release = Published("v0.9.0", "b7d41c9", Sha256Of(NewBuild));
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.UpToDate);
        handler.Exchanges.Should().BeEmpty();
        File.ReadAllText(installed).Should().Be(InstalledBody);
    }

    [Fact]
    public async Task A_release_whose_asset_is_already_the_installed_file_is_not_fetched_again()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        File.WriteAllBytes(installed, NewBuild);

        var handler = new RecordingHandler(_ => RecordingHandler.Bytes(NewBuild));
        using var http = new HttpClient(handler);

        var release = Published("v1.0.1", "b7d41c9", Sha256Of(NewBuild));
        var client = new RecordedReleaseClient(ReleaseLookup.Found(release));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.UpToDate);
        handler.Exchanges.Should().BeEmpty();
        File.ReadAllBytes(installed).Should().Equal(NewBuild);

        new InstalledAppStore(root.Paths).ReadIdentity()!.Version.Should().Be("1.0.1");
    }

    [Fact]
    public async Task A_failed_lookup_leaves_the_installed_build_alone()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        var handler = new RecordingHandler(_ => RecordingHandler.Bytes(NewBuild));
        using var http = new HttpClient(handler);

        var client = new RecordedReleaseClient(ReleaseLookup.Failed("GitHub could not be reached."));
        var workflow = new UpdateWorkflow(client, http, root.Paths, identity: Identity("1.0.0"));

        var status = await workflow.CheckFetchAndApplyAsync(CancellationToken.None);

        status.Outcome.Should().Be(UpdateOutcome.CheckFailed);
        handler.Exchanges.Should().BeEmpty();
        File.ReadAllText(installed).Should().Be(InstalledBody);
    }

    [Fact]
    public async Task A_staged_payload_that_no_longer_matches_is_refused_at_the_moment_of_install()
    {
        using var root = new TemporaryUpdateRoot();
        var installed = Install(root);

        File.WriteAllBytes(root.Paths.StagedPayload, NewBuild);

        var staged = new StagedUpdate
        {
            Tag = "v1.0.1",
            Version = "1.0.1",
            Commit = "b7d41c9",
            Sha256 = Sha256Of(NewBuild),
            SizeBytes = NewBuild.Length,
            StagedUtc = DateTimeOffset.UtcNow,
            File = root.Paths.StagedPayload,
        };

        new StagedUpdateStore(root.Paths.ReadyFile).Write(staged);

        File.WriteAllText(root.Paths.StagedPayload, "something else entirely");

        var verdict = await new UpdateApplier(root.Paths)
            .ApplyAsync(installed, staged, staged.Sha256, CancellationToken.None);

        verdict.State.Should().Be(ApplyState.Refused);
        verdict.InstalledBuildTouched.Should().BeFalse();

        File.ReadAllText(installed).Should().Be(InstalledBody);
        File.Exists(root.Paths.StagedPayload).Should().BeFalse();
        File.Exists(root.Paths.ReadyFile).Should().BeFalse();
    }

    [Fact]
    public async Task An_install_target_that_is_not_the_application_is_refused()
    {
        using var root = new TemporaryUpdateRoot();
        Install(root);

        var elsewhere = Path.Combine(root.Root, "app", "something-else.exe");
        File.WriteAllText(elsewhere, "not the app");

        File.WriteAllBytes(root.Paths.StagedPayload, NewBuild);

        var staged = new StagedUpdate
        {
            Version = "1.0.1",
            Sha256 = Sha256Of(NewBuild),
            SizeBytes = NewBuild.Length,
            File = root.Paths.StagedPayload,
        };

        var verdict = await new UpdateApplier(root.Paths)
            .ApplyAsync(elsewhere, staged, staged.Sha256, CancellationToken.None);

        verdict.State.Should().Be(ApplyState.Refused);
        File.ReadAllText(elsewhere).Should().Be("not the app");
    }

    [Fact]
    public void A_checksums_file_is_read_by_asset_name()
    {
        var body =
            "# BetterTranslator 1.0.1\n"
            + "aaaaaaaabbbbbbbbccccccccddddddddeeeeeeeeffffffff0000000011111111 *BetterTranslator.exe\n"
            + "bbbbbbbbccccccccddddddddeeeeeeeeffffffff00000000111111112222222 checksums.txt\n";

        PayloadVerifier.FindChecksum(body, "BetterTranslator.exe")
            .Should().Be("aaaaaaaabbbbbbbbccccccccddddddddeeeeeeeeffffffff0000000011111111");

        PayloadVerifier.FindChecksum(body, "nothing-here.exe").Should().BeNull();
        PayloadVerifier.FindChecksum(string.Empty, "BetterTranslator.exe").Should().BeNull();
    }

    [Fact]
    public async Task A_payload_with_no_expected_checksum_is_refused_by_the_verifier_itself()
    {
        using var root = new TemporaryUpdateRoot();

        var file = Path.Combine(root.Paths.StagingFolder, "payload.bin");
        File.WriteAllBytes(file, NewBuild);

        var verdict = await PayloadVerifier.VerifyAsync(file, null, NewBuild.Length, CancellationToken.None);

        verdict.Accepted.Should().BeFalse();
        verdict.Detail.Should().Contain("checksum");
    }
}
