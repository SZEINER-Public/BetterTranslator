using BetterTranslator.Core.Services;

namespace BetterTranslator.Updates.Releases;

public enum UpdateOutcome
{
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

public sealed record UpdateComparison(
    UpdateOutcome Outcome,
    string Detail,
    string InstalledVersion,
    string InstalledCommit,
    string LatestVersion,
    string LatestCommit)
{
    public bool IsUpdate => Outcome == UpdateOutcome.UpdateAvailable;
}

public static class UpdateComparer
{
    private const int ShortCommitLength = 7;
    private const int MaxTagInDetail = 48;

    public static UpdateComparison Compare(BuildIdentity installed, ReleaseLookup lookup) =>
        lookup is { Ok: true, Release: { } release }
            ? Compare(installed, release)
            : Failed(installed, lookup.Detail.Length > 0 ? lookup.Detail : "The latest release could not be read.");

    public static UpdateComparison Compare(BuildIdentity installed, ReleaseInfo release)
    {
        if (!SemanticVersion.TryParse(installed.Version, out var current))
        {
            return Failed(
                installed,
                $"This build reports \"{Readable(installed.Version)}\", which is not a version this can compare.");
        }

        if (!SemanticVersion.TryParse(release.Tag, out var latest))
        {
            return Failed(
                installed,
                $"The latest release is tagged \"{Readable(release.Tag)}\", which is not a version this can compare.");
        }

        var installedCommit = installed.HasCommit ? installed.Commit : string.Empty;
        var latestVersion = latest.ToString();
        var order = latest.CompareTo(current);

        if (order > 0)
        {
            return new UpdateComparison(
                UpdateOutcome.UpdateAvailable,
                $"{latestVersion} is newer than the installed {installed.Version}.",
                installed.Version,
                installedCommit,
                latestVersion,
                release.Commit);
        }

        if (order < 0)
        {
            return new UpdateComparison(
                UpdateOutcome.UpToDate,
                $"Release {latestVersion} is older than the installed {installed.Version}, so it stays where it is.",
                installed.Version,
                installedCommit,
                latestVersion,
                release.Commit);
        }

        if (installedCommit.Length > 0 && release.HasCommit && !SameCommit(installedCommit, release.Commit))
        {
            return new UpdateComparison(
                UpdateOutcome.UpdateAvailable,
                $"{latestVersion} was published from commit {Short(release.Commit)} and this build came from "
                + $"{Short(installedCommit)}.",
                installed.Version,
                installedCommit,
                latestVersion,
                release.Commit);
        }

        return new UpdateComparison(
            UpdateOutcome.UpToDate,
            $"{latestVersion} is the latest release.",
            installed.Version,
            installedCommit,
            latestVersion,
            release.Commit);
    }

    public static bool SameCommit(string? left, string? right)
    {
        var mine = left?.Trim() ?? string.Empty;
        var theirs = right?.Trim() ?? string.Empty;

        if (mine.Length < ShortCommitLength || theirs.Length < ShortCommitLength)
        {
            return false;
        }

        var shared = Math.Min(mine.Length, theirs.Length);

        return mine.AsSpan(0, shared).Equals(theirs.AsSpan(0, shared), StringComparison.OrdinalIgnoreCase);
    }

    public static string Short(string commit) =>
        commit.Length > ShortCommitLength ? commit[..ShortCommitLength] : commit;

    private static UpdateComparison Failed(BuildIdentity installed, string detail) =>
        new(UpdateOutcome.CheckFailed,
            detail,
            installed.Version,
            installed.HasCommit ? installed.Commit : string.Empty,
            string.Empty,
            string.Empty);

    private static string Readable(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length > MaxTagInDetail)
        {
            trimmed = trimmed[..MaxTagInDetail];
        }

        return new string(trimmed.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }
}
