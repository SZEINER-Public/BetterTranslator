using System.Runtime.InteropServices;

namespace BetterTranslator.Bench;

internal sealed record TelemetryProbe(bool Working, string Mechanism, IReadOnlyList<string> Attempts, string Consequence);

internal sealed record EnvironmentRecord(
    string RunId,
    string CapturedUtc,
    string ClaudeVersion,
    string DotnetSdkVersion,
    string OperatingSystem,
    string Model,
    string RepositoryPath,
    string GitCommit,
    string GitStatusAtStart,
    string SourceLanguage,
    string TargetLanguage,
    McpProbeResult Mcp,
    TelemetryProbe Telemetry,
    CorpusManifest? Corpus,
    string? StoppedBecause);

internal static class EnvironmentProbe
{
    internal static EnvironmentRecord Capture(
        string runId,
        string model,
        string repositoryPath,
        string sourceLanguage,
        string targetLanguage,
        McpProbeResult mcp,
        TelemetryProbe telemetry,
        CorpusManifest? corpus,
        string? stoppedBecause)
    {
        return new EnvironmentRecord(
            RunId: runId,
            CapturedUtc: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ClaudeVersion: Shell.Line("claude", ["--version"]),
            DotnetSdkVersion: Shell.Line("dotnet", ["--version"]),
            OperatingSystem: $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            Model: model,
            RepositoryPath: repositoryPath,
            GitCommit: Shell.Line("git", ["-C", Paths.RepoRoot, "rev-parse", "HEAD"]),
            GitStatusAtStart: Shell.Capture("git", ["-C", Paths.RepoRoot, "status", "--porcelain"]).StandardOutput.Trim(),
            SourceLanguage: sourceLanguage,
            TargetLanguage: targetLanguage,
            Mcp: mcp,
            Telemetry: telemetry,
            Corpus: corpus,
            StoppedBecause: stoppedBecause);
    }
}
