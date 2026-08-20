using System.IO;
using BetterTranslator.App.Notifications;
using BetterTranslator.App.Services;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Notifications;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class UpdateNotificationTests
{
    private const string Executable = @"C:\Apps\BetterTranslator\BetterTranslator.exe";

    private static BuildIdentity Installed(string version = "1.0.0", string commit = "a54ff15") =>
        new(version, commit, "stable", DateTimeOffset.UnixEpoch);

    private static PendingUpdateNotice Waiting(string version = "1.0.1", string commit = "b7d41c9") =>
        new()
        {
            Tag = "v" + version,
            Version = version,
            Commit = commit,
            InstalledVersion = "1.0.0",
            PublishedUtc = new DateTimeOffset(2026, 8, 18, 7, 15, 0, TimeSpan.Zero),
            Notes = "Faster startup and a fix for the retrieval map.",
            PageUrl = "https://github.com/SZEINER-Public/BetterTranslator/releases/tag/v" + version,
            StagedUtc = DateTimeOffset.UnixEpoch,
        };

    [Theory]
    [InlineData("action=install&version=1.0.1&commit=b7d41c9", ToastAction.Install, "1.0.1", "b7d41c9")]
    [InlineData("action=later&version=1.0.1", ToastAction.Later, "1.0.1", "")]
    [InlineData("action=open", ToastAction.Open, "", "")]
    public void A_valid_activation_payload_parses(string payload, ToastAction action, string version, string commit)
    {
        ToastActivationPayload.TryParse(payload, out var activation).Should().BeTrue();

        activation.Action.Should().Be(action);
        activation.Version.Should().Be(version);
        activation.Commit.Should().Be(commit);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("action")]
    [InlineData("action=")]
    [InlineData("=install")]
    [InlineData("action=restart")]
    [InlineData("action=install&launch=calc.exe")]
    [InlineData("action=install&version=1.0.1;rm -rf")]
    [InlineData(@"action=install&version=..\..\windows\system32")]
    [InlineData("action=install&commit=zzzz")]
    [InlineData("action=install&&version=1.0.1")]
    public void A_malformed_activation_payload_is_refused(string? payload)
    {
        ToastActivationPayload.TryParse(payload, out var activation).Should().BeFalse();

        activation.Version.Should().BeEmpty();
        activation.Commit.Should().BeEmpty();
    }

    [Fact]
    public void An_oversized_activation_payload_is_refused_before_it_is_read()
    {
        var payload = "action=install&version=" + new string('1', ToastActivationPayload.MaxLength);

        payload.Length.Should().BeGreaterThan(ToastActivationPayload.MaxLength);

        ToastActivationPayload.TryParse(payload, out _).Should().BeFalse();
    }

    [Fact]
    public void What_is_built_is_what_is_parsed_back()
    {
        var payload = ToastActivationPayload.Build(ToastAction.Install, "1.0.1", "b7d41c9");

        payload.Length.Should().BeLessThanOrEqualTo(ToastActivationPayload.MaxLength);

        ToastActivationPayload.TryParse(payload, out var activation).Should().BeTrue();

        activation.Action.Should().Be(ToastAction.Install);
        activation.Identity.Should().Be("1.0.1+b7d41c9");
    }

    [Fact]
    public void Release_text_is_escaped_rather_than_pasted_into_the_notification()
    {
        var notice = Waiting() with
        {
            Notes = "<image src=\"file://evil\"/> & \"quoted\" 'text'",
        };

        var xml = ToastContent.Build(notice, "1.0.0", null);

        xml.Should().NotContain("<image src=\"file://evil\"");
        xml.Should().Contain("&lt;image");
        xml.Should().Contain("&amp;");
        System.Xml.Linq.XDocument.Parse(xml).Should().NotBeNull();
    }

    [Fact]
    public void A_control_character_in_the_release_body_never_reaches_the_notification()
    {
        ToastContent.Notes("first line\nsecond\u0007line").Should().Be("first line second line");
        ToastContent.Notes(new string('a', 400)).Length.Should().BeLessThanOrEqualTo(180);
    }

    [Fact]
    public void The_notification_carries_both_versions_the_actions_and_a_replaceable_tag()
    {
        var xml = ToastContent.Build(Waiting(), "1.0.0", null);
        var document = System.Xml.Linq.XDocument.Parse(xml);

        xml.Should().Contain("BetterTranslator 1.0.1 is available");
        xml.Should().Contain("You are on 1.0.0.");

        document.Root!.Attribute("scenario").Should().BeNull("the default scenario respects quiet hours");

        var actions = document.Root.Element("actions")!.Elements("action").ToArray();

        actions.Should().HaveCount(3);
        actions[0].Attribute("arguments")!.Value.Should().Be("action=install&version=1.0.1&commit=b7d41c9");
        actions[1].Attribute("arguments")!.Value.Should().Be("action=later&version=1.0.1&commit=b7d41c9");
        actions[2].Attribute("activationType")!.Value.Should().Be("protocol");
        actions[2].Attribute("arguments")!.Value.Should().StartWith("https://github.com/");

        ToastContent.Tag.Should().Be("update");
        ToastContent.Group.Should().Be("BetterTranslator.Update");
    }

    [Fact]
    public void A_release_page_that_is_not_a_github_https_address_is_dropped()
    {
        var xml = ToastContent.Build(
            Waiting() with { PageUrl = "file:///C:/Windows/System32/calc.exe" },
            "1.0.0",
            null);

        var actions = System.Xml.Linq.XDocument.Parse(xml).Root!.Element("actions")!.Elements("action").ToArray();

        actions.Should().HaveCount(2);
        xml.Should().NotContain("calc.exe");
    }

    [Fact]
    public void One_release_identity_is_announced_once()
    {
        using var state = new TemporaryUserState();

        var channel = new FakeNotificationChannel();
        var notifications = Build(channel, state, out var pending);

        pending.Write(Waiting()).Should().BeTrue();

        notifications.RaisePending().Raised.Should().BeTrue();
        notifications.RaisePending().Raised.Should().BeFalse();

        channel.Shown.Should().ContainSingle();
    }

    [Fact]
    public void A_rebuild_of_the_same_version_from_a_different_commit_is_announced_again()
    {
        using var state = new TemporaryUserState();

        var channel = new FakeNotificationChannel();
        var notifications = Build(channel, state, out var pending);

        pending.Write(Waiting("1.0.1", "b7d41c9"));
        notifications.RaisePending().Raised.Should().BeTrue();

        pending.Write(Waiting("1.0.1", "ff01234"));
        notifications.RaisePending().Raised.Should().BeTrue();

        channel.Shown.Should().HaveCount(2);
    }

    [Fact]
    public void The_build_already_running_is_never_announced()
    {
        using var state = new TemporaryUserState();

        var channel = new FakeNotificationChannel();
        var notifications = Build(channel, state, out var pending);

        pending.Write(Waiting("1.0.0", "a54ff15"));

        notifications.RaisePending().Raised.Should().BeFalse();
        channel.Shown.Should().BeEmpty();
        pending.Read().Should().BeNull();
    }

    [Fact]
    public void A_notification_that_cannot_be_raised_leaves_the_notice_for_the_window()
    {
        using var state = new TemporaryUserState();

        var channel = new FakeNotificationChannel { Refuse = true };
        var notifications = Build(channel, state, out var pending);

        pending.Write(Waiting());

        notifications.RaisePending().Raised.Should().BeFalse();

        pending.Read().Should().NotBeNull("the in-app banner is the fallback at next launch");
        state.Notified.Read().Should().BeNull("a notification nobody saw was not announced");
    }

    [Fact]
    public async Task Choosing_later_records_the_dismissal_and_replaces_nothing()
    {
        using var state = new TemporaryUserState();

        var channel = new FakeNotificationChannel();
        var install = new FakeInstallCoordinator();
        var notifications = Build(channel, state, out _, install);

        var reply = await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Later, "1.0.1", "b7d41c9"),
            inTheRunningApp: true,
            CancellationToken.None);

        reply.Ok.Should().BeTrue();
        install.HandOvers.Should().Be(0);
        state.Notified.Read()!.Dismissed.Should().BeTrue();
        state.Notified.WasShown("1.0.1+b7d41c9").Should().BeTrue();
    }

    [Fact]
    public async Task Clicking_the_notification_body_opens_one_application_and_no_second_copy()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator();
        var notifications = Build(new FakeNotificationChannel(), state, out _, install);

        await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Open, "1.0.1", "b7d41c9"),
            inTheRunningApp: false,
            CancellationToken.None);

        install.Launches.Should().Be(1);

        await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Open, "1.0.1", "b7d41c9"),
            inTheRunningApp: true,
            CancellationToken.None);

        install.Launches.Should().Be(1, "a running application handles the click itself");
    }

    [Fact]
    public async Task A_malformed_activation_is_refused_without_touching_the_install()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator();
        var notifications = Build(new FakeNotificationChannel(), state, out _, install);

        var reply = await notifications.ActivateAsync(
            new string('a', 4096),
            inTheRunningApp: true,
            CancellationToken.None);

        reply.Ok.Should().BeFalse();
        install.HandOvers.Should().Be(0);
        install.CloseRequests.Should().Be(0);
    }

    [Fact]
    public async Task Install_now_hands_over_when_the_location_is_the_users_own()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator { LocationIsWritable = true };
        var notifications = Build(new FakeNotificationChannel(), state, out _, install);

        var reply = await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Install, "1.0.1", "b7d41c9"),
            inTheRunningApp: true,
            CancellationToken.None);

        reply.Ok.Should().BeTrue();
        install.HandOvers.Should().Be(1);
        install.ServiceApplies.Should().Be(0);
        install.Elevations.Should().Be(0);
    }

    [Fact]
    public async Task Install_now_asks_the_open_application_to_close_first()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator { AnotherInstanceIsRunning = true, CloseSucceeds = true };
        var notifications = Build(new FakeNotificationChannel(), state, out _, install);

        var reply = await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Install, "1.0.1", "b7d41c9"),
            inTheRunningApp: false,
            CancellationToken.None);

        reply.Ok.Should().BeTrue();
        install.CloseRequests.Should().Be(1);
        install.HandOvers.Should().Be(1);
    }

    [Fact]
    public async Task An_application_that_will_not_close_aborts_the_install_and_keeps_the_download()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator { AnotherInstanceIsRunning = true, CloseSucceeds = false };
        var channel = new FakeNotificationChannel();
        var notifications = Build(channel, state, out var pending, install);

        pending.Write(Waiting());

        var reply = await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Install, "1.0.1", "b7d41c9"),
            inTheRunningApp: false,
            CancellationToken.None);

        reply.Ok.Should().BeFalse();
        reply.Detail.Should().Contain("still open");
        install.HandOvers.Should().Be(0);
        pending.Read().Should().NotBeNull();
        channel.Withdrawals.Should().Be(0);
    }

    [Fact]
    public async Task An_unwritable_location_is_handed_to_the_service_when_there_is_one()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator { LocationIsWritable = false, ServiceIsInstalled = true };
        var notifications = Build(new FakeNotificationChannel(), state, out _, install);

        await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Install, "1.0.1", "b7d41c9"),
            inTheRunningApp: true,
            CancellationToken.None);

        install.ServiceApplies.Should().Be(1);
        install.HandOvers.Should().Be(0);
        install.Elevations.Should().Be(0);
    }

    [Fact]
    public async Task An_unwritable_location_with_no_service_asks_once_for_administrator_rights()
    {
        using var state = new TemporaryUserState();

        var install = new FakeInstallCoordinator { LocationIsWritable = false, ServiceIsInstalled = false };
        var notifications = Build(new FakeNotificationChannel(), state, out _, install);

        await notifications.ActivateAsync(
            ToastActivationPayload.Build(ToastAction.Install, "1.0.1", "b7d41c9"),
            inTheRunningApp: true,
            CancellationToken.None);

        install.Elevations.Should().Be(1);
        install.HandOvers.Should().Be(0);
    }

    [Theory]
    [InlineData(true, true, InstallRoute.Direct)]
    [InlineData(true, false, InstallRoute.Direct)]
    [InlineData(false, true, InstallRoute.ServiceHandoff)]
    [InlineData(false, false, InstallRoute.Elevate)]
    public void The_install_route_follows_the_location_and_the_service(bool writable, bool service, InstallRoute route)
    {
        InstallLocation.Decide(writable, service).Should().Be(route);
    }

    [Fact]
    public void Writability_is_answered_by_writing_rather_than_by_guessing()
    {
        using var state = new TemporaryUserState();

        InstallLocation.IsWritable(Path.Combine(state.Root, "BetterTranslator.exe")).Should().BeTrue();
        InstallLocation.IsWritable(Path.Combine(state.Root, "gone", "BetterTranslator.exe")).Should().BeFalse();

        Directory.GetFiles(state.Root).Should().BeEmpty("the probe cleans up after itself");
    }

    [Fact]
    public void A_running_instance_is_detected_by_the_name_it_holds()
    {
        var name = @"Local\BetterTranslator.Tests." + Guid.NewGuid().ToString("N");

        RunningInstance.Exists(name).Should().BeFalse();

        using (var instance = new AppInstance(name))
        {
            instance.IsOnlyInstance.Should().BeTrue();
            RunningInstance.Exists(name).Should().BeTrue();

            using var second = new AppInstance(name);

            second.IsOnlyInstance.Should().BeFalse();
        }

        RunningInstance.Exists(name).Should().BeFalse();
    }

    [Fact]
    public void Registration_writes_once_and_stays_quiet_afterwards()
    {
        var registry = new FakeRegistryStore();
        var shortcuts = new FakeShortcutStore();
        var registration = new ShellRegistration(registry, shortcuts, Executable, @"C:\Start Menu\BetterTranslator.lnk");

        registration.IsRegistered.Should().BeFalse();

        var first = registration.Register();

        first.Registered.Should().BeTrue();
        first.Changed.Should().BeTrue();

        var second = registration.Register();

        second.Registered.Should().BeTrue();
        second.Changed.Should().BeFalse();

        registry.Writes.Should().HaveCount(2);
        shortcuts.Created.Should().ContainSingle();
        registration.IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void Registration_stays_under_the_current_user_and_names_the_activator()
    {
        var registry = new FakeRegistryStore();
        var shortcuts = new FakeShortcutStore();

        new ShellRegistration(registry, shortcuts, Executable, @"C:\Start Menu\BetterTranslator.lnk").Register();

        registry.Writes.Should().OnlyContain(key => key.StartsWith(@"Software\Classes\CLSID\", StringComparison.Ordinal));
        registry.Values[ShellIdentity.LocalServerKey].Should().Be($"\"{Executable}\" -ToastActivated");
        shortcuts.AppUserModelId.Should().Be(ShellIdentity.AppUserModelId);
        shortcuts.Activator.Should().Be(ShellIdentity.Activator);
    }

    [Fact]
    public void Removal_takes_the_shortcut_and_the_activator_key_back_out()
    {
        var registry = new FakeRegistryStore();
        var shortcuts = new FakeShortcutStore();
        var registration = new ShellRegistration(registry, shortcuts, Executable, @"C:\Start Menu\BetterTranslator.lnk");

        registration.Register();
        registration.Remove();

        registration.IsRegistered.Should().BeFalse();
        registry.Removals.Should().ContainSingle().Which.Should().Be(ShellIdentity.ClsidKey);
        shortcuts.Removed.Should().ContainSingle();
    }

    [Fact]
    public void A_shortcut_that_cannot_be_written_reports_registration_as_failed()
    {
        var registration = new ShellRegistration(
            new FakeRegistryStore(),
            new FakeShortcutStore { Refuse = true },
            Executable,
            @"C:\Start Menu\BetterTranslator.lnk");

        registration.Register().Registered.Should().BeFalse();
    }

    [Fact]
    public void The_registry_store_refuses_a_key_outside_the_activator_branch()
    {
        var store = new UserRegistryStore();

        store.Invoking(s => s.Write(@"Software\Microsoft\Windows\CurrentVersion\Run", "x"))
            .Should().Throw<ArgumentException>();

        store.Invoking(s => s.Read(@"Software\Classes\Applications"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_identity_is_the_version_and_the_short_commit()
    {
        ReleaseIdentity.Of("1.0.1", "b7d41c9e2f3a5061728394a5b6c7d8e9f0a1b2c3").Should().Be("1.0.1+b7d41c9");
        ReleaseIdentity.Of("1.0.1", string.Empty).Should().Be("1.0.1");

        ReleaseIdentity.Same("1.0.1+b7d41c9", "1.0.1+B7D41C9").Should().BeTrue();
        ReleaseIdentity.Same("1.0.1+b7d41c9", "1.0.1+ff01234").Should().BeFalse();
        ReleaseIdentity.Same(string.Empty, string.Empty).Should().BeFalse();
    }

    [Fact]
    public void A_pending_notice_is_read_back_only_when_it_is_sound()
    {
        using var root = new TemporaryUpdateRoot();

        var store = new PendingNoticeStore(root.Paths);

        store.Read().Should().BeNull();

        store.Write(Waiting()).Should().BeTrue();
        store.Read()!.Identity.Should().Be("1.0.1+b7d41c9");

        store.Write(Waiting() with { Version = string.Empty }).Should().BeFalse();

        File.WriteAllText(store.File, "{ not json");
        store.Read().Should().BeNull();
    }

    [Fact]
    public void A_notice_carrying_a_hostile_page_address_loses_it_on_the_way_in()
    {
        using var root = new TemporaryUpdateRoot();

        var store = new PendingNoticeStore(root.Paths);

        store.Write(Waiting() with { PageUrl = "javascript:alert(1)" }).Should().BeTrue();

        store.Read()!.PageUrl.Should().BeEmpty();
    }

    private static UpdateNotifications Build(
        FakeNotificationChannel channel,
        TemporaryUserState state,
        out PendingNoticeStore pending,
        IInstallCoordinator? install = null)
    {
        var paths = new UpdatePaths(Path.Combine(state.Root, "programdata"));

        pending = new PendingNoticeStore(paths);

        var registry = new FakeRegistryStore();
        var shortcuts = new FakeShortcutStore();
        var registration = new ShellRegistration(registry, shortcuts, Executable, Path.Combine(state.Root, "app.lnk"));

        registration.Register();

        return new UpdateNotifications(
            channel,
            registration,
            pending,
            state.Notified,
            Installed(),
            _ => install ?? new FakeInstallCoordinator());
    }
}
