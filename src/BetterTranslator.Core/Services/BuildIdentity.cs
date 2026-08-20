using System.Reflection;

namespace BetterTranslator.Core.Services;

public sealed record BuildIdentity(
    string Version,
    string Commit,
    string Channel,
    DateTimeOffset BuildTimestampUtc)
{
    public const string UnknownCommit = "0000000";

    public static BuildIdentity Current { get; } = Read(typeof(BuildIdentity).Assembly);

    public bool HasCommit => Commit.Length > 0 && !string.Equals(Commit, UnknownCommit, StringComparison.OrdinalIgnoreCase);

    public string Display => HasCommit ? $"{Version}+{Commit}" : Version;

    public static BuildIdentity Read(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? string.Empty;

        var separator = informational.IndexOf('+');

        var version = separator >= 0 ? informational[..separator] : informational;
        var commit = separator >= 0 ? informational[(separator + 1)..] : string.Empty;

        if (version.Length == 0)
        {
            version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();

        return new BuildIdentity(
            version.Trim(),
            Normalize(commit),
            Metadata(metadata, "BuildChannel") is { Length: > 0 } channel ? channel : "stable",
            Timestamp(Metadata(metadata, "BuildTimestampUtc")));
    }

    private static string Normalize(string commit)
    {
        var trimmed = commit.Trim();

        return string.Equals(trimmed, UnknownCommit, StringComparison.OrdinalIgnoreCase) ? string.Empty : trimmed;
    }

    private static string Metadata(AssemblyMetadataAttribute[] attributes, string key) =>
        attributes.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))?.Value?.Trim()
        ?? string.Empty;

    private static DateTimeOffset Timestamp(string stamped)
    {
        if (DateTimeOffset.TryParse(
                stamped,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        try
        {
            if (Environment.ProcessPath is { Length: > 0 } executable && File.Exists(executable))
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(executable), TimeSpan.Zero);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return DateTimeOffset.UnixEpoch;
    }
}
