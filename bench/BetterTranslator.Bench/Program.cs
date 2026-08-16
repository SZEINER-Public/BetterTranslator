using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BetterTranslator.Bench;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var command = args.Length > 0 ? args[0] : "help";
var options = Options.Parse(args.Skip(1));

switch (command)
{
    case "prepare":
        return Prepare(options);
    case "run":
        return Run(options);
    case "collect":
        return CollectRun(options);
    case "compare":
        return CompareRun(options);
    default:
        Console.WriteLine("btbench prepare --source <file> --model <model> [--sizes 10,50,200,500] [--from en] [--to cs]");
        Console.WriteLine("btbench run --run <run-id> [--reps 3] [--max-turns 60] [--base-seconds 600] [--per-key-seconds 6] [--arms direct,mcp-path,mcp-value] [--slices 10,50,200,500]");
        Console.WriteLine("btbench collect --run <run-id>");
        Console.WriteLine("btbench compare --run <run-id>");
        return 1;
}

static int Prepare(Options options)
{
    var source = options.Require("source");
    var model = options.Require("model");
    var sizes = options.Value("sizes", "10,50,200,500").Split(',').Select(int.Parse).ToList();
    var from = options.Value("from", "en");
    var to = options.Value("to", "cs");
    var runId = options.Value("run", NewRunId());

    var executable = options.Value("mcp-executable", McpExecutable());
    var probe = McpProbe.Probe(executable, "mcp", TimeSpan.FromSeconds(90));

    var telemetry = new TelemetryProbe(
        Working: false,
        Mechanism: "stream-json events and the print mode result object",
        Attempts:
        [
            "CLAUDE_CODE_ENABLE_TELEMETRY=1 with OTEL_METRICS_EXPORTER=console and OTEL_LOGS_EXPORTER=console produced no output on stdout or stderr",
            "the same with --debug produced no output",
            "OTEL_METRICS_EXPORTER=otlp with OTEL_EXPORTER_OTLP_PROTOCOL=http/json against a loopback receiver delivered no request",
            "the same exporter set supplied through --settings env delivered no request",
        ],
        Consequence: "this Claude Code build emits no OpenTelemetry metrics or events, so per-request accounting is read from Claude's own stream-json usage blocks and the print mode result object instead. The prescribed telemetry environment is still set on every child process.");

    string? stopped = null;

    if (!probe.Reachable)
    {
        stopped = $"the MCP server did not answer: {probe.Detail}";
    }
    else if (!probe.TranslateFilePresent)
    {
        stopped = "the MCP server does not expose translate_file";
    }
    else if (!probe.TranslateFileAcceptsInputPath || !probe.TranslateFileAcceptsOutputPath)
    {
        stopped = "translate_file does not accept both an input path and an output path";
    }
    else if (probe.SelectedModel.Length == 0)
    {
        stopped = "no local translation model is selected";
    }

    var corpus = stopped is null ? Corpus.Build(source, sizes) : null;
    var record = EnvironmentProbe.Capture(runId, model, Path.GetDirectoryName(Path.GetFullPath(source))!, from, to, probe, telemetry, corpus, stopped);

    Directory.CreateDirectory(Paths.RunDirectory(runId));
    Files.WriteJson(Path.Combine(Paths.RunDirectory(runId), "environment.json"), record);

    Console.WriteLine(runId);

    if (stopped is not null)
    {
        Console.Error.WriteLine($"stopped: {stopped}");
        return 2;
    }

    foreach (var slice in corpus!.Slices)
    {
        Console.WriteLine($"{slice.Name} keys={slice.Keys} bytes={slice.Bytes} sha256={slice.Sha256}");
    }

    return 0;
}

static int Run(Options options)
{
    var runId = options.Require("run");
    var reps = int.Parse(options.Value("reps", "3"));
    var maxTurns = int.Parse(options.Value("max-turns", "60"));
    var baseSeconds = int.Parse(options.Value("base-seconds", "600"));
    var perKeySeconds = double.Parse(options.Value("per-key-seconds", "6"));
    var environmentPath = Path.Combine(Paths.RunDirectory(runId), "environment.json");

    if (!File.Exists(environmentPath))
    {
        Console.Error.WriteLine($"{environmentPath} is missing; run prepare first.");
        return 2;
    }

    var environment = JsonSerializer.Deserialize<EnvironmentRecord>(File.ReadAllText(environmentPath))!;

    if (environment.StoppedBecause is not null || environment.Corpus is null)
    {
        Console.Error.WriteLine($"stopped: {environment.StoppedBecause ?? "no corpus"}");
        return 2;
    }

    var armFilter = options.Value("arms", string.Empty);
    var sliceFilter = options.Value("slices", string.Empty);
    var arms = Runner.Arms
        .Where(arm => armFilter.Length == 0 || armFilter.Split(',').Contains(arm.Name))
        .ToList();
    var slices = environment.Corpus.Slices
        .Where(slice => sliceFilter.Length == 0 || sliceFilter.Split(',').Contains(slice.Keys.ToString()))
        .ToList();

    var done = Runner.Load(runId)
        .Select(outcome => $"{outcome.Spec.Arm}:{outcome.Spec.SliceKeys}:{outcome.Spec.Rep}")
        .ToHashSet(StringComparer.Ordinal);

    foreach (var slice in slices)
    {
        foreach (var rep in Enumerable.Range(1, reps))
        {
            foreach (var arm in arms)
            {
                var identity = $"{arm.Name}:{slice.Keys}:{rep}";

                if (done.Contains(identity))
                {
                    Console.WriteLine($"skip {identity}");
                    continue;
                }

                var promptPath = Path.Combine(Paths.Prompts, arm.PromptFile);
                var spec = new RunSpec(
                    RunId: runId,
                    Arm: arm.Name,
                    Slice: slice.Name,
                    SliceKeys: slice.Keys,
                    Rep: rep,
                    InputPath: slice.Path,
                    OutputPath: Path.Combine(Paths.ScratchDirectory(runId), $"{arm.Name}-{slice.Name}-{rep}.json"),
                    LogPath: Path.Combine(Paths.LogDirectory(runId), $"{arm.Name}-{slice.Name}-{rep}.log"),
                    SessionId: Guid.NewGuid().ToString(),
                    MaxTurns: maxTurns,
                    TimeoutSeconds: baseSeconds + (int)(perKeySeconds * slice.Keys),
                    PromptBytes: (int)new FileInfo(promptPath).Length);

                Console.WriteLine($"start {identity} timeout={spec.TimeoutSeconds}s");
                var outcome = Runner.Execute(spec, environment.Model);
                Runner.Append(runId, outcome);
                Console.WriteLine($"done  {identity} exit={outcome.ExitCode} timedOut={outcome.TimedOut} wall={outcome.WallClockMs / 1000.0:0.0}s");
            }
        }
    }

    return 0;
}

static int CollectRun(Options options)
{
    var runId = options.Require("run");
    var outcomes = Runner.Load(runId);

    if (outcomes.Count == 0)
    {
        Console.Error.WriteLine("no runs recorded");
        return 2;
    }

    var summaries = Collect.Summarise(runId, outcomes);

    Collect.WriteRaw(runId, summaries);
    Collect.WriteSummary(runId, summaries);

    var verdict = Report.Write(runId, summaries);

    Console.WriteLine(verdict);

    return 0;
}

static int CompareRun(Options options)
{
    var runId = options.Require("run");
    var outcomes = Runner.Load(runId);

    if (outcomes.Count == 0)
    {
        Console.Error.WriteLine("no runs recorded");
        return 2;
    }

    Console.WriteLine(Compare.Write(runId, Collect.Summarise(runId, outcomes)));

    return 0;
}

static string NewRunId()
{
    var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    return $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{suffix}";
}

static string McpExecutable() => Path.Combine(
    Paths.RepoRoot,
    "src",
    "BetterTranslator.App",
    "bin",
    "Debug",
    "net10.0-windows10.0.19041.0",
    "cli",
    "bt.exe");

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    internal static Options Parse(IEnumerable<string> arguments)
    {
        var options = new Options();
        string? pending = null;

        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    options._values[pending] = "true";
                }

                pending = argument[2..];
            }
            else if (pending is not null)
            {
                options._values[pending] = argument;
                pending = null;
            }
        }

        if (pending is not null)
        {
            options._values[pending] = "true";
        }

        return options;
    }

    internal string Value(string name, string fallback) => _values.GetValueOrDefault(name, fallback);

    internal string Require(string name) => _values.TryGetValue(name, out var value)
        ? value
        : throw new ArgumentException($"--{name} is required");
}
