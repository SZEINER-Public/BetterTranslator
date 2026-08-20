using System.Runtime.Versioning;
using BetterTranslator.Updates;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Ipc;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Notifications;
using BetterTranslator.Updates.Releases;
using BetterTranslator.Updates.Service;

namespace BetterTranslator.UpdateService;

[SupportedOSPlatform("windows")]
internal sealed class UpdateAnnouncer(UpdatePaths paths, IUpdateLog log)
{
    private const string NotifyVerb = "--notify-update";

    private readonly PendingNoticeStore _pending = new(paths);
    private readonly InstalledAppStore _installed = new(paths);

    public async Task AnnounceAsync(UpdateStatus status, ReleaseInfo? release, CancellationToken cancellationToken)
    {
        if (!status.Ready || status.LatestVersion.Length == 0)
        {
            _pending.Clear();

            return;
        }

        var notice = new PendingUpdateNotice
        {
            Tag = release?.Tag ?? string.Empty,
            Version = status.LatestVersion,
            Commit = status.LatestCommit,
            InstalledVersion = status.InstalledVersion,
            PublishedUtc = release?.PublishedUtc ?? DateTimeOffset.UnixEpoch,
            Notes = release?.Notes ?? string.Empty,
            PageUrl = release?.PageUrl ?? string.Empty,
            StagedUtc = DateTimeOffset.UtcNow,
        }.Sanitized();

        if (!notice.IsUsable)
        {
            return;
        }

        if (_pending.Read() is { } already && ReleaseIdentity.Same(already.Identity, notice.Identity))
        {
            return;
        }

        if (!_pending.Write(notice))
        {
            log.Write("The pending update notice could not be written, so nothing was announced.");

            return;
        }

        await DeliverAsync(notice, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync(PendingUpdateNotice notice, CancellationToken cancellationToken)
    {
        var session = InteractiveSession.ActiveSessionId();

        if (session < 0)
        {
            log.Write($"{notice.Version} is staged. No one is signed in, so the application announces it at next start.");

            return;
        }

        var app = new AppInstanceClient(AppInstanceProtocol.PipeName(session));
        var answered = await app.AskAsync(AppInstanceVerb.Notify, string.Empty, cancellationToken).ConfigureAwait(false);

        if (answered is { Ok: true })
        {
            log.Write($"{notice.Version} was announced by the running application.");

            return;
        }

        if (_installed.Read() is not { } executable)
        {
            log.Write("No installed application is recorded, so the notice waits for the next start.");

            return;
        }

        var launched = InteractiveSession.Launch(executable, NotifyVerb);

        log.Write($"Announcing {notice.Version} in session {session}: {launched.Detail}");
    }
}
