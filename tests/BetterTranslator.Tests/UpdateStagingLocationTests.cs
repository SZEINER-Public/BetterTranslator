using System.IO;
using BetterTranslator.App.Services;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Payload;
using BetterTranslator.Updates.Service;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Registering the updater locks ProgramData\BetterTranslator down to SYSTEM and
/// the administrators, so that a standard user cannot leave a payload there for
/// a SYSTEM service to install. A download the user asks for therefore cannot
/// stage there, and goes under their own profile instead.
/// </summary>
public sealed class UpdateStagingLocationTests
{
    [Fact]
    public void A_download_this_user_asks_for_stages_where_this_user_can_always_write()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        UpdatePaths.ForCurrentUser().Root.Should().StartWith(local);
        UpdatePaths.ForCurrentUser().Root.Should().NotBe(UpdatePaths.DefaultRoot);
    }

    [Fact]
    public async Task A_build_staged_by_this_user_is_offered_when_the_shared_folder_holds_nothing()
    {
        using var shared = new TemporaryUpdateRoot();
        using var mine = new TemporaryUpdateRoot();

        Stage(mine.Paths, "9.9.9");

        var gateway = new UpdaterGateway(shared.Paths, mine.Paths, () => ServiceState.NotInstalled);

        var ready = await gateway.ReadyAsync(CancellationToken.None);

        ready.Should().NotBeNull("the user staged it themselves and nothing else will find it");
        ready!.LatestVersion.Should().Be("9.9.9");
        ready.Ready.Should().BeTrue();
    }

    [Fact]
    public async Task A_build_the_service_staged_is_preferred_over_one_in_the_user_profile()
    {
        using var shared = new TemporaryUpdateRoot();
        using var mine = new TemporaryUpdateRoot();

        Stage(shared.Paths, "9.9.9");
        Stage(mine.Paths, "8.8.8");

        var gateway = new UpdaterGateway(shared.Paths, mine.Paths, () => ServiceState.NotInstalled);

        var ready = await gateway.ReadyAsync(CancellationToken.None);

        ready.Should().NotBeNull();
        ready!.LatestVersion.Should().Be(
            "9.9.9",
            "the service verified its payload as SYSTEM in a folder no standard user can write");
    }

    [Fact]
    public async Task Nothing_staged_anywhere_offers_nothing()
    {
        using var shared = new TemporaryUpdateRoot();
        using var mine = new TemporaryUpdateRoot();

        var gateway = new UpdaterGateway(shared.Paths, mine.Paths, () => ServiceState.NotInstalled);

        (await gateway.ReadyAsync(CancellationToken.None)).Should().BeNull();
    }

    private static void Stage(UpdatePaths paths, string version)
    {
        paths.EnsureCreated();

        string payload = Path.Combine(paths.StagingFolder, UpdatePaths.PayloadAssetName);
        File.WriteAllBytes(payload, [1, 2, 3]);

        new StagedUpdateStore(paths.ReadyFile).Write(new StagedUpdate
        {
            Tag = "v" + version,
            Version = version,
            Commit = "c0ffee1",
            Sha256 = new string('a', 64),
            SizeBytes = 3,
            StagedUtc = DateTimeOffset.UnixEpoch,
            File = payload,
        });
    }
}
