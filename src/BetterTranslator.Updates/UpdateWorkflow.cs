using BetterTranslator.Core.Services;
using BetterTranslator.Updates.Install;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Payload;
using BetterTranslator.Updates.Releases;

namespace BetterTranslator.Updates;

public sealed record UpdateStatus(
    UpdateOutcome Outcome,
    string Detail,
    string InstalledVersion,
    string InstalledCommit,
    string LatestVersion,
    string LatestCommit,
    bool Ready,
    DateTimeOffset CheckedUtc)
{
    public static UpdateStatus From(UpdateComparison comparison, bool ready, DateTimeOffset checkedUtc) =>
        new(comparison.Outcome,
            comparison.Detail,
            comparison.InstalledVersion,
            comparison.InstalledCommit,
            comparison.LatestVersion,
            comparison.LatestCommit,
            ready,
            checkedUtc);

    public static UpdateStatus Failure(BuildIdentity installed, string detail, bool ready) =>
        new(UpdateOutcome.CheckFailed,
            detail,
            installed.Version,
            installed.HasCommit ? installed.Commit : string.Empty,
            string.Empty,
            string.Empty,
            ready,
            DateTimeOffset.UtcNow);
}

public sealed class UpdateWorkflow
{
    private readonly IReleaseClient _client;
    private readonly UpdateDownloader _downloader;
    private readonly UpdateApplier _applier;
    private readonly StagedUpdateStore _staged;
    private readonly InstalledAppStore _installed;
    private readonly UpdatePaths _paths;
    private readonly IUpdateLog _log;
    private readonly BuildIdentity? _declared;

    public UpdateWorkflow(
        IReleaseClient client,
        HttpClient http,
        UpdatePaths paths,
        IUpdateLog? log = null,
        BuildIdentity? identity = null)
    {
        _client = client;
        _paths = paths;
        _log = log ?? NullUpdateLog.Instance;
        _declared = identity;
        _downloader = new UpdateDownloader(http, paths, _log);
        _applier = new UpdateApplier(paths, _log);
        _staged = new StagedUpdateStore(paths.ReadyFile);
        _installed = new InstalledAppStore(paths);
    }

    public BuildIdentity Identity => _declared ?? _installed.ReadIdentity() ?? BuildIdentity.Current;

    public StagedUpdate? Staged => _staged.Read();

    public ReleaseInfo? LastRelease { get; private set; }

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var lookup = await _client.LatestAsync(cancellationToken).ConfigureAwait(false);
        LastRelease = lookup.Release;

        var comparison = UpdateComparer.Compare(Identity, lookup);

        return UpdateStatus.From(comparison, _staged.Read() is not null, DateTimeOffset.UtcNow);
    }

    public async Task<UpdateStatus> CheckFetchAndApplyAsync(CancellationToken cancellationToken)
    {
        var lookup = await _client.LatestAsync(cancellationToken).ConfigureAwait(false);
        LastRelease = lookup.Release;

        var comparison = UpdateComparer.Compare(Identity, lookup);

        _log.Write($"Checked for updates: {comparison.Outcome}. {comparison.Detail}");

        if (!comparison.IsUpdate || lookup.Release is null)
        {
            if (_staged.Read() is not null)
            {
                _applier.Discard();
            }

            return UpdateStatus.From(comparison, ready: false, DateTimeOffset.UtcNow);
        }

        var expected = await _downloader.ExpectedChecksumAsync(lookup.Release, cancellationToken).ConfigureAwait(false);

        if (expected is not null && await IsAlreadyInstalledAsync(expected, comparison, cancellationToken).ConfigureAwait(false))
        {
            _applier.Discard();

            return UpdateStatus.From(comparison, ready: false, DateTimeOffset.UtcNow) with
            {
                Outcome = UpdateOutcome.UpToDate,
                Detail = $"{comparison.LatestVersion} is the build already installed here.",
            };
        }

        if (_staged.Read() is { } already && SameBuild(already, comparison) && expected is not null)
        {
            return await ApplyStagedAsync(already, comparison, expected, cancellationToken).ConfigureAwait(false);
        }

        _applier.Discard();

        var fetched = await _downloader
            .FetchAsync(lookup.Release, comparison, expected, cancellationToken)
            .ConfigureAwait(false);

        if (!fetched.Staged || fetched.Update is null)
        {
            return UpdateStatus.From(comparison, ready: false, DateTimeOffset.UtcNow) with
            {
                Outcome = UpdateOutcome.CheckFailed,
                Detail = fetched.Detail,
            };
        }

        return await ApplyStagedAsync(fetched.Update, comparison, expected!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateStatus> ApplyStagedAsync(CancellationToken cancellationToken)
    {
        var staged = _staged.Read();

        if (staged is null)
        {
            return UpdateStatus.Failure(Identity, "Nothing is staged to install.", ready: false);
        }

        var lookup = await _client.LatestAsync(cancellationToken).ConfigureAwait(false);
        var comparison = UpdateComparer.Compare(Identity, lookup);

        if (lookup.Release is null)
        {
            return UpdateStatus.Failure(
                Identity,
                "The published checksum could not be read, so the staged build was not installed: " + comparison.Detail,
                ready: true);
        }

        var expected = await _downloader.ExpectedChecksumAsync(lookup.Release, cancellationToken).ConfigureAwait(false);

        if (expected is null)
        {
            return UpdateStatus.Failure(
                Identity,
                $"Release {lookup.Release.Tag} publishes no checksum, so the staged build was not installed.",
                ready: true);
        }

        return await ApplyStagedAsync(staged, comparison, expected, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateStatus> ApplyStagedAsync(
        StagedUpdate staged,
        UpdateComparison comparison,
        string expected,
        CancellationToken cancellationToken)
    {
        var executable = _installed.Read();

        if (executable is null)
        {
            _log.Write("No installed build is recorded, so the staged payload stays where it is.");

            return UpdateStatus.From(comparison, ready: true, DateTimeOffset.UtcNow) with
            {
                Detail = $"{staged.Version} is staged. The installed build was not recorded, so nothing was replaced.",
            };
        }

        var verdict = await _applier.ApplyAsync(executable, staged, expected, cancellationToken).ConfigureAwait(false);

        return verdict.State switch
        {
            ApplyState.Applied => Recorded(executable, comparison, verdict.Detail),
            ApplyState.HeldForRestart => UpdateStatus.From(comparison, ready: true, DateTimeOffset.UtcNow) with
            {
                Detail = verdict.Detail,
            },
            ApplyState.HeldForReboot => UpdateStatus.From(comparison, ready: false, DateTimeOffset.UtcNow) with
            {
                Detail = verdict.Detail,
            },
            _ => UpdateStatus.From(comparison, _staged.Read() is not null, DateTimeOffset.UtcNow) with
            {
                Outcome = UpdateOutcome.CheckFailed,
                Detail = verdict.Detail,
            },
        };
    }

    private UpdateStatus Recorded(string executable, UpdateComparison comparison, string detail)
    {
        try
        {
            _installed.Write(executable, comparison.LatestVersion, comparison.LatestCommit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("The installed version could not be recorded", ex);
        }

        return UpdateStatus.From(comparison, ready: false, DateTimeOffset.UtcNow) with
        {
            Outcome = UpdateOutcome.UpToDate,
            Detail = detail,
        };
    }

    private async Task<bool> IsAlreadyInstalledAsync(
        string expected,
        UpdateComparison comparison,
        CancellationToken cancellationToken)
    {
        var executable = _installed.Read();

        if (executable is null)
        {
            return false;
        }

        try
        {
            var actual = await PayloadVerifier.ComputeSha256Async(executable, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _installed.Write(executable, comparison.LatestVersion, comparison.LatestCommit);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("The installed build could not be read to compare it with the release", ex);

            return false;
        }
    }

    public void ForgetStaged() => _applier.Discard();

    public void RemoveSupersededBuilds(string installedExecutable) =>
        _applier.RemoveSupersededBuilds(installedExecutable);

    public UpdatePaths Paths => _paths;

    private static bool SameBuild(StagedUpdate staged, UpdateComparison comparison) =>
        string.Equals(staged.Version, comparison.LatestVersion, StringComparison.OrdinalIgnoreCase)
        && (comparison.LatestCommit.Length == 0
            || UpdateComparer.SameCommit(staged.Commit, comparison.LatestCommit));
}
