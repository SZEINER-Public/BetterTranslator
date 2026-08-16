using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BetterTranslator.Bench;

internal sealed record ArmDefinition(string Name, string PromptFile, IReadOnlyList<string> ExtraArguments);

internal sealed record RunSpec(
    string RunId,
    string Arm,
    string Slice,
    int SliceKeys,
    int Rep,
    string InputPath,
    string OutputPath,
    string LogPath,
    string SessionId,
    int MaxTurns,
    int TimeoutSeconds,
    int PromptBytes);

internal sealed record RunOutcome(
    RunSpec Spec,
    int ExitCode,
    bool TimedOut,
    long WallClockMs,
    string StartedUtc);

internal static class Runner
{
    internal static IReadOnlyList<ArmDefinition> Arms { get; } =
    [
        new("direct", "direct.txt", ["--strict-mcp-config"]),
        new("mcp-path", "mcp-path.txt", ["--mcp-config", Paths.McpConfig, "--strict-mcp-config"]),
        new("mcp-value", "mcp-value.txt", ["--mcp-config", Paths.McpConfig, "--strict-mcp-config"]),
    ];

    internal static RunOutcome Execute(RunSpec spec, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(spec.LogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(spec.OutputPath)!);

        if (File.Exists(spec.OutputPath))
        {
            File.Delete(spec.OutputPath);
        }

        var arm = Arms.Single(candidate => candidate.Name == spec.Arm);
        var template = File.ReadAllText(Path.Combine(Paths.Prompts, arm.PromptFile), Files.Utf8NoBom);
        var prompt = template
            .Replace("{{INPUT}}", spec.InputPath, StringComparison.Ordinal)
            .Replace("{{OUTPUT}}", spec.OutputPath, StringComparison.Ordinal);

        var start = new ProcessStartInfo("claude")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Paths.RepoRoot,
        };

        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(prompt);
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(model);
        start.ArgumentList.Add("--output-format");
        start.ArgumentList.Add("stream-json");
        start.ArgumentList.Add("--verbose");
        start.ArgumentList.Add("--session-id");
        start.ArgumentList.Add(spec.SessionId);
        start.ArgumentList.Add("--max-turns");
        start.ArgumentList.Add(spec.MaxTurns.ToString());
        start.ArgumentList.Add("--permission-mode");
        start.ArgumentList.Add("bypassPermissions");

        foreach (var argument in arm.ExtraArguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["CLAUDE_CODE_ENABLE_TELEMETRY"] = "1";
        start.Environment["OTEL_METRICS_EXPORTER"] = "console";
        start.Environment["OTEL_LOGS_EXPORTER"] = "console";
        start.Environment["OTEL_METRIC_EXPORT_INTERVAL"] = "1000";
        start.Environment["OTEL_LOG_TOOL_DETAILS"] = "1";
        start.Environment["OTEL_RESOURCE_ATTRIBUTES"] =
            $"bench.run={spec.RunId},bench.arm={spec.Arm},bench.slice={spec.SliceKeys},bench.rep={spec.Rep}";

        var startedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var stopwatch = Stopwatch.StartNew();

        using var log = new StreamWriter(spec.LogPath, false, Files.Utf8NoBom) { AutoFlush = true };
        using var errorLog = new StreamWriter(spec.LogPath + ".err", false, Files.Utf8NoBom) { AutoFlush = true };
        using var process = new Process { StartInfo = start };

        var drained = new ManualResetEventSlim(false);
        var errorDrained = new ManualResetEventSlim(false);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                drained.Set();
            }
            else
            {
                log.WriteLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorDrained.Set();
            }
            else
            {
                errorLog.WriteLine(e.Data);
            }
        };

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = !process.WaitForExit(spec.TimeoutSeconds * 1000);

        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            process.WaitForExit(30000);
        }

        drained.Wait(TimeSpan.FromSeconds(10));
        errorDrained.Wait(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        return new RunOutcome(spec, timedOut ? -1 : process.ExitCode, timedOut, stopwatch.ElapsedMilliseconds, startedUtc);
    }

    internal static void Append(string runId, RunOutcome outcome)
    {
        var path = Path.Combine(Paths.RunDirectory(runId), "runs.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(outcome, JsonOptions.Compact) + "\n", Files.Utf8NoBom);
    }

    internal static IReadOnlyList<RunOutcome> Load(string runId)
    {
        var path = Path.Combine(Paths.RunDirectory(runId), "runs.jsonl");

        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonSerializer.Deserialize<RunOutcome>(line)!)
            .ToList();
    }
}
