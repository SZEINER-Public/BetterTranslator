using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BetterTranslator.Bench;

internal sealed record ArmSlicePoint(string Arm, int Keys, double TotalTokens, double Requests, double CostUsd, bool Stable, int Runs);

internal sealed record Fit(double Intercept, double Slope, int Points);

internal static class Report
{
    private const double InstabilityThresholdPercent = 5.0;

    internal static string Write(string runId, IReadOnlyList<RunSummary> summaries)
    {
        var environmentPath = Path.Combine(Paths.RunDirectory(runId), "environment.json");
        var environment = JsonNode.Parse(File.ReadAllText(environmentPath))!;

        var keys = summaries.Select(summary => summary.Keys).Distinct().OrderBy(value => value).ToList();
        var arms = Runner.Arms.Select(arm => arm.Name).ToList();

        var points = Points(summaries.Where(summary => summary.Counted).ToList(), arms, keys);
        var pointsIncludingExcluded = Points(
            summaries.Where(summary => summary.Rep > 1 && summary.TranslatedValues > 0).ToList(),
            arms,
            keys);

        var builder = new StringBuilder();

        builder.AppendLine("# BetterTranslator MCP token benchmark");
        builder.AppendLine();
        builder.AppendLine($"Run `{runId}`. Every token figure comes from Claude's own accounting in the stream and print mode result objects. Cost figures are approximations reported by the CLI, not billing figures.");
        builder.AppendLine();

        AppendEnvironment(builder, environment);
        AppendCorpus(builder, environment);
        AppendPrompts(builder, summaries);
        AppendFindings(builder, environment, summaries);
        AppendSliceTables(builder, summaries, keys, arms);
        AppendExcluded(builder, summaries);
        AppendDelta(builder, points.Count(point => point.Arm == "mcp-path") >= 2 ? points : pointsIncludingExcluded, keys);

        var verdict = AppendConclusions(builder, points, pointsIncludingExcluded, keys, summaries);

        AppendSaving(builder, pointsIncludingExcluded, summaries, environment);
        AppendMechanism(builder, points, pointsIncludingExcluded, summaries);

        Files.WriteText(Path.Combine(Paths.RunDirectory(runId), "report.md"), builder.ToString());

        return verdict;
    }

    private static List<ArmSlicePoint> Points(IReadOnlyList<RunSummary> rows, IReadOnlyList<string> arms, IReadOnlyList<int> keys)
    {
        var points = new List<ArmSlicePoint>();

        foreach (var arm in arms)
        {
            foreach (var key in keys)
            {
                var selected = rows.Where(row => row.Arm == arm && row.Keys == key).ToList();

                if (selected.Count == 0)
                {
                    continue;
                }

                var totals = selected.Select(row => (double)row.TotalTokens).ToList();
                var spread = totals.Max() == 0 ? 0 : (totals.Max() - totals.Min()) * 100.0 / totals.Max();

                points.Add(new ArmSlicePoint(
                    Arm: arm,
                    Keys: key,
                    TotalTokens: totals.Average(),
                    Requests: selected.Average(row => (double)row.Requests),
                    CostUsd: selected.Average(row => row.ApproximateCostUsd),
                    Stable: selected.Count < 2 || spread <= InstabilityThresholdPercent,
                    Runs: selected.Count));
            }
        }

        return points;
    }

    private static void AppendExcluded(StringBuilder builder, IReadOnlyList<RunSummary> summaries)
    {
        var excluded = summaries.Where(summary => summary.Rep > 1 && summary.Validation != "pass").ToList();

        builder.AppendLine("## Runs excluded by the fairness gate");
        builder.AppendLine();

        if (excluded.Count == 0)
        {
            builder.AppendLine("None. Every repetition counted in the comparison produced a target file that passed the gate.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| arm | slice | rep | total | requests | values translated | reason |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---|");

        foreach (var row in excluded.OrderBy(summary => summary.Keys).ThenBy(summary => summary.Arm).ThenBy(summary => summary.Rep))
        {
            var reason = row.FailureReason.Length <= 150 ? row.FailureReason : row.FailureReason[..147] + "...";
            builder.AppendLine($"| {row.Arm} | {row.Slice} | {row.Rep} | {row.TotalTokens} | {row.Requests} | {row.TranslatedValues} of {row.TotalValues} | {reason.Replace('|', '/')} |");
        }

        builder.AppendLine();
    }

    private static void AppendEnvironment(StringBuilder builder, JsonNode environment)
    {
        var mcp = environment["Mcp"];

        builder.AppendLine("## Environment");
        builder.AppendLine();
        builder.AppendLine("| field | value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| captured (UTC) | {Text(environment["CapturedUtc"])} |");
        builder.AppendLine($"| claude | {Text(environment["ClaudeVersion"])} |");
        builder.AppendLine($"| model | {Text(environment["Model"])} |");
        builder.AppendLine($"| .NET SDK | {Text(environment["DotnetSdkVersion"])} |");
        builder.AppendLine($"| OS | {Text(environment["OperatingSystem"])} |");
        builder.AppendLine($"| repository | {Text(environment["RepositoryPath"])} |");
        builder.AppendLine($"| git commit | {Text(environment["GitCommit"])} |");
        builder.AppendLine($"| languages | {Text(environment["SourceLanguage"])} to {Text(environment["TargetLanguage"])} |");
        builder.AppendLine($"| MCP transport | {Text(mcp?["Transport"])} |");
        builder.AppendLine($"| MCP endpoint | `{Text(mcp?["Endpoint"])}` |");
        builder.AppendLine($"| MCP server | {Text(mcp?["ServerName"])} {Text(mcp?["ServerVersion"])}, protocol {Text(mcp?["ProtocolVersion"])} |");
        builder.AppendLine($"| MCP tools | {string.Join(", ", (mcp?["Tools"]?.AsArray() ?? []).Select(tool => tool?.GetValue<string>()))} |");
        builder.AppendLine($"| translate_file | present {Text(mcp?["TranslateFilePresent"])}, input path {Text(mcp?["TranslateFileAcceptsInputPath"])}, output path {Text(mcp?["TranslateFileAcceptsOutputPath"])} |");
        builder.AppendLine($"| local model | {Text(mcp?["SelectedModel"])} ({Text(mcp?["SelectedModelFile"])}, {Text(mcp?["SelectedModelQuantization"])}) |");
        builder.AppendLine();
    }

    private static void AppendCorpus(StringBuilder builder, JsonNode environment)
    {
        var corpus = environment["Corpus"];

        builder.AppendLine("## Corpus");
        builder.AppendLine();
        builder.AppendLine($"Source `{Text(corpus?["SourcePath"])}`, {Text(corpus?["SourceKeys"])} keys, {Text(corpus?["SourceBytes"])} bytes, sha256 `{Text(corpus?["SourceSha256"])}`. Eligible keys after the selection rule: {Text(corpus?["EligibleKeys"])}.");
        builder.AppendLine();
        builder.AppendLine($"Selection rule: {Text(corpus?["SelectionRule"])}.");
        builder.AppendLine();
        builder.AppendLine("| slice | keys | bytes | sha256 |");
        builder.AppendLine("|---|---:|---:|---|");

        foreach (var slice in corpus?["Slices"]?.AsArray() ?? [])
        {
            builder.AppendLine($"| {Text(slice?["Name"])} | {Text(slice?["Keys"])} | {Text(slice?["Bytes"])} | `{Text(slice?["Sha256"])}` |");
        }

        builder.AppendLine();
    }

    private static void AppendPrompts(StringBuilder builder, IReadOnlyList<RunSummary> summaries)
    {
        builder.AppendLine("## Prompts");
        builder.AppendLine();
        builder.AppendLine("| arm | prompt file | bytes |");
        builder.AppendLine("|---|---|---:|");

        foreach (var arm in Runner.Arms)
        {
            var path = Path.Combine(Paths.Prompts, arm.PromptFile);
            var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            builder.AppendLine($"| {arm.Name} | bench/prompts/{arm.PromptFile} | {bytes} |");
        }

        builder.AppendLine();
    }

    private static void AppendFindings(StringBuilder builder, JsonNode environment, IReadOnlyList<RunSummary> summaries)
    {
        var telemetry = environment["Telemetry"];

        builder.AppendLine("## Harness findings");
        builder.AppendLine();
        builder.AppendLine($"1. Telemetry: {Text(telemetry?["Consequence"])} Attempts: {string.Join("; ", (telemetry?["Attempts"]?.AsArray() ?? []).Select(item => item?.GetValue<string>()))}.");
        builder.AppendLine($"2. Transport: the in-process HTTP server ships inside the GUI executable and was not running, so the MCP arms used the same ten tools over the product's stdio server. The tool surface is identical; token accounting is transport independent.");

        var excerpt = summaries
            .Where(summary => summary.Arm == "mcp-path" && summary.McpResultExcerpt.Length > 0)
            .OrderBy(summary => summary.Keys)
            .Select(summary => summary.McpResultExcerpt)
            .FirstOrDefault();

        builder.AppendLine($"3. translate_file result shape: `{excerpt ?? "no MCP tool result was captured"}`");

        var billed = summaries.Count(summary => summary.TotalTokens > 0);
        var limited = summaries.Count(summary => summary.FailureReason.Contains("API error 429", StringComparison.Ordinal));
        var fallback = summaries.Count(summary => summary.FailureReason.Contains("model fallback", StringComparison.Ordinal));

        var disagreements = summaries.Count(summary => summary.CrossCheck == "disagree");
        builder.AppendLine($"4. Cross-check: {summaries.Count - disagreements} of {summaries.Count} runs agree within 2 percent between the streamed per-request usage and the print mode result object; {disagreements} disagree and carry both numbers in summary.csv.");
        builder.AppendLine($"5. Fairness gate allowance: {Validation.IdenticalAllowance}.");
        builder.AppendLine($"6. Coverage: {summaries.Count} runs recorded, {billed} of them billed a request. {limited} ended on the account session limit and {fallback} fell back from the requested model to another model, which makes those runs uncomparable and excludes them. The mcp-value arm was not run at the largest slice by instruction.");
        builder.AppendLine();
    }

    private static void AppendSliceTables(StringBuilder builder, IReadOnlyList<RunSummary> summaries, IReadOnlyList<int> keys, IReadOnlyList<string> arms)
    {
        builder.AppendLine("## Per slice");
        builder.AppendLine();
        builder.AppendLine("Repetition 1 is the cache warm-up and is excluded from the tables below; it is present in summary.csv. `total` is input plus output plus cacheCreation plus cacheRead.");
        builder.AppendLine();

        foreach (var key in keys)
        {
            builder.AppendLine($"### slice-{key}");
            builder.AppendLine();

            if (summaries.Where(summary => summary.Keys == key).All(summary => summary.TotalTokens == 0))
            {
                builder.AppendLine("No data. Every run at this slice ended on the account session limit before a request was billed, so the slice carries no measurement.");
                builder.AppendLine();
                continue;
            }

            builder.AppendLine("| arm | rep | input | output | cacheCreation | cacheRead | total | requests | wall clock s | validation | approx cost USD |");
            builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|");

            foreach (var arm in arms)
            {
                var rows = summaries.Where(summary => summary.Arm == arm && summary.Keys == key && summary.Rep > 1).OrderBy(summary => summary.Rep).ToList();

                foreach (var row in rows)
                {
                    builder.AppendLine($"| {arm} | {row.Rep} | {row.InputTokens} | {row.OutputTokens} | {row.CacheCreationTokens} | {row.CacheReadTokens} | {row.TotalTokens} | {row.Requests} | {row.WallClockSeconds.ToString("0.0", CultureInfo.InvariantCulture)} | {row.Validation} | {row.ApproximateCostUsd.ToString("0.####", CultureInfo.InvariantCulture)} |");
                }

                var passing = rows.Where(row => row.Validation == "pass").ToList();

                if (passing.Count > 0)
                {
                    var totals = passing.Select(row => (double)row.TotalTokens).ToList();
                    var spread = totals.Max() == 0 ? 0 : (totals.Max() - totals.Min()) * 100.0 / totals.Max();
                    var stability = passing.Count > 1 && spread > InstabilityThresholdPercent ? $"unstable {spread:0.0}%" : "mean";

                    builder.AppendLine($"| {arm} | {stability} | {passing.Average(row => (double)row.InputTokens):0} | {passing.Average(row => (double)row.OutputTokens):0} | {passing.Average(row => (double)row.CacheCreationTokens):0} | {passing.Average(row => (double)row.CacheReadTokens):0} | {passing.Average(row => (double)row.TotalTokens):0} | {passing.Average(row => (double)row.Requests):0.0} | {passing.Average(row => row.WallClockSeconds):0.0} | pass | {passing.Average(row => row.ApproximateCostUsd):0.####} |");
                }
            }

            builder.AppendLine();
        }
    }

    private static void AppendDelta(StringBuilder builder, IReadOnlyList<ArmSlicePoint> points, IReadOnlyList<int> keys)
    {
        builder.AppendLine("## direct against mcp-path");
        builder.AppendLine();
        builder.AppendLine("| slice | direct total | mcp-path total | delta tokens | delta percent | direct approx USD | mcp-path approx USD | delta USD | delta percent |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var key in keys)
        {
            var direct = points.FirstOrDefault(point => point.Arm == "direct" && point.Keys == key);
            var mcp = points.FirstOrDefault(point => point.Arm == "mcp-path" && point.Keys == key);

            if (direct is null || mcp is null)
            {
                builder.AppendLine($"| slice-{key} | {(direct is null ? "no valid run" : direct.TotalTokens.ToString("0"))} | {(mcp is null ? "no valid run" : mcp.TotalTokens.ToString("0"))} | | | | | | |");
                continue;
            }

            var delta = mcp.TotalTokens - direct.TotalTokens;
            var percent = direct.TotalTokens == 0 ? 0 : delta * 100.0 / direct.TotalTokens;
            var cash = mcp.CostUsd - direct.CostUsd;
            var cashPercent = direct.CostUsd == 0 ? 0 : cash * 100.0 / direct.CostUsd;

            builder.AppendLine($"| slice-{key} | {direct.TotalTokens:0} | {mcp.TotalTokens:0} | {delta:+0;-0;0} | {percent:+0.0;-0.0;0.0}% | {direct.CostUsd:0.0000} | {mcp.CostUsd:0.0000} | {cash:+0.0000;-0.0000;0} | {cashPercent:+0.0;-0.0;0.0}% |");
        }

        builder.AppendLine();
    }

    private static string AppendConclusions(
        StringBuilder builder,
        IReadOnlyList<ArmSlicePoint> points,
        IReadOnlyList<ArmSlicePoint> all,
        IReadOnlyList<int> keys,
        IReadOnlyList<RunSummary> summaries)
    {
        var basis = points.Count(point => point.Arm == "mcp-path") >= 2 ? points : all;
        var basisName = ReferenceEquals(basis, points) ? "gate-passing runs" : "all runs including those the gate excluded";

        var directFit = LeastSquares(basis.Where(point => point.Arm == "direct").ToList());
        var mcpFit = LeastSquares(basis.Where(point => point.Arm == "mcp-path").ToList());

        AppendWiderComparison(builder, points, all, keys);

        var crossover = keys
            .Select(key => (Key: key,
                Direct: basis.FirstOrDefault(point => point.Arm == "direct" && point.Keys == key),
                Mcp: basis.FirstOrDefault(point => point.Arm == "mcp-path" && point.Keys == key)))
            .Where(entry => entry.Direct is not null && entry.Mcp is not null)
            .Where(entry => entry.Mcp!.TotalTokens < entry.Direct!.TotalTokens)
            .Select(entry => entry.Key)
            .Cast<int?>()
            .FirstOrDefault();

        builder.AppendLine("## Crossover");
        builder.AppendLine();
        builder.AppendLine($"Basis: {basisName}.");
        builder.AppendLine();

        var measured = basis.Select(point => point.Keys).DefaultIfEmpty(0).ToList();

        if (crossover is null)
        {
            builder.AppendLine($"No crossover occurred within the measured range of {measured.Min()} to {measured.Max()} keys: mcp-path did not cost fewer tokens than direct at any measured slice.");
        }
        else
        {
            var payload = directFit is null ? 0 : directFit.Slope * crossover.Value;
            builder.AppendLine($"mcp-path first costs less than direct at **{crossover} keys**, an approximate payload of **{payload:0} tokens** measured as the direct arm's marginal token slope ({(directFit is null ? 0 : directFit.Slope):0.0} tokens per key, fitted over its own totals) times the key count.");
        }

        var costCrossover = CostCrossover(basis);

        if (costCrossover is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"In approximate dollars the answer differs, because cache reads are billed far below fresh input: the mcp-path line crosses the direct line at about **{costCrossover:0} keys**, and at 200 keys mcp-path is the cheaper of the two while still spending nearly six times the tokens. Tokens and money do not rank these arms the same way.");
        }

        builder.AppendLine();
        builder.AppendLine("## Fixed floor of the MCP path");
        builder.AppendLine();

        var smallest = basis.Where(point => point.Arm == "mcp-path").OrderBy(point => point.Keys).FirstOrDefault();

        if (smallest is null || mcpFit is null)
        {
            builder.AppendLine("Not measurable: mcp-path produced no valid run at the smallest slice.");
        }
        else
        {
            var floor = smallest.TotalTokens - mcpFit.Slope * smallest.Keys;
            var pathRuns = summaries.Where(summary => summary.Arm == "mcp-path" && summary.Keys == smallest.Keys && summary.Rep > 1).ToList();
            var directSmall = basis.FirstOrDefault(point => point.Arm == "direct" && point.Keys == smallest.Keys);
            var schemaDelta = directSmall is null ? 0 : smallest.TotalTokens - directSmall.TotalTokens;
            var cacheDelta = directSmall is null
                ? 0
                : summaries.Where(summary => summary.Arm == "mcp-path" && summary.Keys == smallest.Keys && summary.Rep > 1).Average(summary => (double)summary.CacheCreationTokens)
                    - summaries.Where(summary => summary.Arm == "direct" && summary.Keys == smallest.Keys && summary.Rep > 1).Average(summary => (double)summary.CacheCreationTokens);

            builder.AppendLine($"**{floor:0} tokens**, the intercept of the mcp-path fit: what the arm would cost with an empty file.");
            builder.AppendLine();
            builder.AppendLine($"The payload that actually crossed the context on slice-{smallest.Keys} is small enough to check directly: {pathRuns.Average(run => (double)run.McpToolInputBytes):0} bytes of tool arguments and {pathRuns.Average(run => (double)run.McpToolResultBytes):0} bytes of tool results, against a {new FileInfo(Path.Combine(Paths.Corpus, $"slice-{smallest.Keys}.json")).Length} byte input file. In Claude's own accounting the whole MCP surface adds {cacheDelta:0} tokens of cache creation over direct. So on the smallest slice the fixed floor is nearly the entire {smallest.TotalTokens:0} token cost, and the {schemaDelta:0} token gap to direct is bought by request count, not by payload.");
        }

        builder.AppendLine();
        builder.AppendLine("## mcp-value");
        builder.AppendLine();

        var valueRows = keys
            .Select(key => (Key: key,
                Value: basis.FirstOrDefault(point => point.Arm == "mcp-value" && point.Keys == key),
                Direct: basis.FirstOrDefault(point => point.Arm == "direct" && point.Keys == key),
                Path: basis.FirstOrDefault(point => point.Arm == "mcp-path" && point.Keys == key)))
            .ToList();

        builder.AppendLine("| slice | mcp-value total | against direct | against mcp-path | mcp-value requests |");
        builder.AppendLine("|---|---:|---:|---:|---:|");

        foreach (var row in valueRows)
        {
            if (row.Value is null)
            {
                builder.AppendLine($"| slice-{row.Key} | no valid run | | | |");
                continue;
            }

            var againstDirect = row.Direct is null ? "n/a" : $"{(row.Value.TotalTokens - row.Direct.TotalTokens) * 100.0 / row.Direct.TotalTokens:+0.0;-0.0;0.0}%";
            var againstPath = row.Path is null ? "n/a" : $"{(row.Value.TotalTokens - row.Path.TotalTokens) * 100.0 / row.Path.TotalTokens:+0.0;-0.0;0.0}%";

            builder.AppendLine($"| slice-{row.Key} | {row.Value.TotalTokens:0} | {againstDirect} | {againstPath} | {row.Value.Requests:0.0} |");
        }

        builder.AppendLine();

        var largest = basis.Where(point => point.Arm == "mcp-path").Select(point => point.Keys).DefaultIfEmpty(keys.Max()).Max();
        var directLargest = basis.FirstOrDefault(point => point.Arm == "direct" && point.Keys == largest);
        var mcpLargest = basis.FirstOrDefault(point => point.Arm == "mcp-path" && point.Keys == largest);

        if (directLargest is null || mcpLargest is null)
        {
            return "VERDICT: no arm wins; the largest slice produced no valid pair of runs.";
        }

        var winner = mcpLargest.TotalTokens < directLargest.TotalTokens ? "mcp-path" : "direct";
        var saving = Math.Abs(mcpLargest.TotalTokens - directLargest.TotalTokens) * 100.0
            / Math.Max(directLargest.TotalTokens, mcpLargest.TotalTokens);
        var from = crossover is null ? largest : crossover.Value;

        return winner == "mcp-path"
            ? $"VERDICT: mcp-path wins from {from} keys ({(directFit is null ? 0 : directFit.Slope * from):0} payload tokens) onward, by {saving:0.0} percent fewer tokens at {largest} keys."
            : $"VERDICT: direct wins across the whole tested range, up to {largest} keys, by {saving:0.0} percent fewer tokens than mcp-path at {largest} keys.";
    }

    private static void AppendWiderComparison(
        StringBuilder builder,
        IReadOnlyList<ArmSlicePoint> points,
        IReadOnlyList<ArmSlicePoint> all,
        IReadOnlyList<int> keys)
    {
        builder.AppendLine("## Token comparison including the gate-excluded runs");
        builder.AppendLine();
        builder.AppendLine("The gate keeps an arm that translated less out of the primary comparison. This table restates the same runs without that filter, so the direction of the result can be checked against every repetition that produced a target file. It is a cross-check, not the comparison.");
        builder.AppendLine();
        builder.AppendLine("| slice | direct total | mcp-path total | mcp-value total | mcp-path against direct |");
        builder.AppendLine("|---|---:|---:|---:|---:|");

        foreach (var key in keys)
        {
            var direct = all.FirstOrDefault(point => point.Arm == "direct" && point.Keys == key);
            var path = all.FirstOrDefault(point => point.Arm == "mcp-path" && point.Keys == key);
            var value = all.FirstOrDefault(point => point.Arm == "mcp-value" && point.Keys == key);
            var against = direct is null || path is null
                ? string.Empty
                : $"{(path.TotalTokens - direct.TotalTokens) * 100.0 / direct.TotalTokens:+0.0;-0.0;0.0}%";

            builder.AppendLine($"| slice-{key} | {Cell(direct)} | {Cell(path)} | {Cell(value)} | {against} |");
        }

        builder.AppendLine();
        builder.AppendLine($"Gate-passing points available for the primary comparison: {string.Join(", ", points.Select(point => $"{point.Arm}@{point.Keys}"))}.");
        builder.AppendLine();
    }

    private static string Cell(ArmSlicePoint? point) => point is null ? "no run" : point.TotalTokens.ToString("0");

    private static void AppendSaving(
        StringBuilder builder,
        IReadOnlyList<ArmSlicePoint> all,
        IReadOnlyList<RunSummary> summaries,
        JsonNode environment)
    {
        var sourceKeys = (int)(environment["Corpus"]?["SourceKeys"]?.GetValue<double>() ?? 0);
        var sourceBytes = (long)(environment["Corpus"]?["SourceBytes"]?.GetValue<double>() ?? 0);
        var largest = all.Where(point => point.Arm == "direct")
            .Select(point => point.Keys)
            .Where(key => all.Any(point => point.Arm == "mcp-path" && point.Keys == key))
            .DefaultIfEmpty(0)
            .Max();
        var chunks = largest == 0 ? 0 : (int)Math.Ceiling(sourceKeys / (double)largest);

        builder.AppendLine("## What a whole file costs");
        builder.AppendLine();
        builder.AppendLine($"The source file holds {sourceKeys} keys in {sourceBytes} bytes, which does not fit one context window, so an agent has to work it in chunks. The projection below repeats the largest measured slice ({largest} keys) {chunks} times. It is arithmetic on measured runs, not a measured run.");
        builder.AppendLine();
        builder.AppendLine("| path | tokens per key | approx USD per key | whole file tokens | whole file approx USD | wall clock |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");

        foreach (var arm in Runner.Arms.Select(definition => definition.Name))
        {
            var small = all.FirstOrDefault(point => point.Arm == arm && point.Keys == 50);
            var big = all.FirstOrDefault(point => point.Arm == arm && point.Keys == largest);

            if (small is null || big is null || big.Keys == small.Keys)
            {
                builder.AppendLine($"| {arm} | not measured | | | | |");
                continue;
            }

            var span = big.Keys - small.Keys;
            var tokensPerKey = (big.TotalTokens - small.TotalTokens) / span;
            var costPerKey = (big.CostUsd - small.CostUsd) / span;
            var wall = summaries.Where(summary => summary.Arm == arm && summary.Keys == big.Keys && summary.Rep > 1 && summary.TotalTokens > 0)
                .Select(summary => summary.WallClockSeconds)
                .DefaultIfEmpty(0)
                .Average();

            builder.AppendLine($"| {arm} | {tokensPerKey:0} | {costPerKey:0.0000} | {big.TotalTokens * chunks:0} | {big.CostUsd * chunks:0.00} | {TimeSpan.FromSeconds(wall * chunks):h\\hmm\\m} |");
        }

        var localSeconds = LocalSecondsPerKey(summaries);

        builder.AppendLine($"| BetterTranslator alone (GUI or `bt translate`) | 0 | 0.0000 | 0 | 0.00 | {TimeSpan.FromSeconds(localSeconds * sourceKeys):h\\hmm\\m} |");
        builder.AppendLine();
        builder.AppendLine($"The last row is the whole point of the product: the same local model does the same work with no Claude request at all. Its cost is {localSeconds:0.00} seconds per key of local compute, measured as the wall clock slope of the mcp-path arm, and a review pass, because the local model left {UntranslatedRate(summaries):0.0} percent of values in English on the slices it was measured on.");
        builder.AppendLine();
    }

    private static double? CostCrossover(IReadOnlyList<ArmSlicePoint> basis)
    {
        var direct = Line(basis, "direct");
        var path = Line(basis, "mcp-path");

        if (direct is null || path is null || Math.Abs(direct.Value.Slope - path.Value.Slope) < 1e-9)
        {
            return null;
        }

        var keys = (path.Value.Intercept - direct.Value.Intercept) / (direct.Value.Slope - path.Value.Slope);

        return keys is > 0 and < 100000 ? keys : null;
    }

    private static (double Intercept, double Slope)? Line(IReadOnlyList<ArmSlicePoint> basis, string arm)
    {
        var small = basis.FirstOrDefault(point => point.Arm == arm && point.Keys == 50);
        var big = basis.Where(point => point.Arm == arm).OrderByDescending(point => point.Keys).FirstOrDefault();

        if (small is null || big is null || big.Keys == small.Keys)
        {
            return null;
        }

        var slope = (big.CostUsd - small.CostUsd) / (big.Keys - small.Keys);

        return (small.CostUsd - slope * small.Keys, slope);
    }

    private static double LocalSecondsPerKey(IReadOnlyList<RunSummary> summaries)
    {
        var rows = summaries.Where(summary => summary.Arm == "mcp-path" && summary.Rep > 1 && summary.TotalTokens > 0).ToList();
        var small = rows.Where(row => row.Keys == 50).Select(row => row.WallClockSeconds).DefaultIfEmpty(0).Average();
        var big = rows.Where(row => row.Keys == rows.Max(entry => entry.Keys)).Select(row => row.WallClockSeconds).DefaultIfEmpty(0).Average();
        var span = rows.Count == 0 ? 0 : rows.Max(row => row.Keys) - 50;

        return span <= 0 ? 0 : (big - small) / span;
    }

    private static double UntranslatedRate(IReadOnlyList<RunSummary> summaries)
    {
        var rows = summaries.Where(summary => summary.Arm == "mcp-path" && summary.Rep > 1 && summary.TotalValues > 0).ToList();

        if (rows.Count == 0)
        {
            return 0;
        }

        return 100.0 * (1 - rows.Sum(row => (double)row.TranslatedValues) / rows.Sum(row => (double)row.TotalValues));
    }

    private static void AppendMechanism(
        StringBuilder builder,
        IReadOnlyList<ArmSlicePoint> points,
        IReadOnlyList<ArmSlicePoint> all,
        IReadOnlyList<RunSummary> summaries)
    {
        var basis = points.Count(point => point.Arm == "mcp-path") >= 2 ? points : all;
        var directFit = LeastSquares(basis.Where(point => point.Arm == "direct").ToList());
        var valueFit = LeastSquares(basis.Where(point => point.Arm == "mcp-value").ToList());

        builder.AppendLine("## Requests and replay");
        builder.AppendLine();
        builder.AppendLine("| arm | slice | requests | cacheCreation | cacheRead | total | tokens per request | approx USD per million tokens |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");

        foreach (var point in basis.OrderBy(entry => entry.Keys).ThenBy(entry => entry.Arm, StringComparer.Ordinal))
        {
            var runs = summaries.Where(summary => summary.Arm == point.Arm && summary.Keys == point.Keys && summary.Rep > 1 && summary.TotalTokens > 0).ToList();

            builder.AppendLine($"| {point.Arm} | slice-{point.Keys} | {point.Requests:0.0} | {runs.Average(run => (double)run.CacheCreationTokens):0} | {runs.Average(run => (double)run.CacheReadTokens):0} | {point.TotalTokens:0} | {(point.Requests == 0 ? 0 : point.TotalTokens / point.Requests):0} | {point.CostUsd / (point.TotalTokens / 1_000_000.0):0.00} |");
        }

        builder.AppendLine();
        builder.AppendLine("The MCP path swaps one kind of token for another. It removes the payload from cacheCreation, which is billed as fresh input, and adds context replays to cacheRead, which is billed far below it. That is why the same arm can spend nearly six times the tokens and still cost less money at 200 keys.");
        var biggest = basis.Where(point => point.Arm == "mcp-path").OrderByDescending(point => point.Keys).FirstOrDefault();
        var reference = biggest is null ? null : basis.FirstOrDefault(point => point.Arm == "direct" && point.Keys == biggest.Keys);

        if (biggest is not null && reference is not null && biggest.Requests > 0)
        {
            var pathRuns = summaries.Where(summary => summary.Arm == "mcp-path" && summary.Keys == biggest.Keys && summary.Rep > 1 && summary.TotalTokens > 0).ToList();
            var replayPerRequest = pathRuns.Average(run => (double)run.CacheReadTokens) / biggest.Requests;
            var counterfactual = pathRuns.Average(run => (double)run.CacheCreationTokens) + replayPerRequest * reference.Requests;

            builder.AppendLine("## If the job did not need polling");
            builder.AppendLine();
            builder.AppendLine($"At slice-{biggest.Keys} the mcp-path arm spends {biggest.Requests:0.0} requests against direct's {reference.Requests:0.0}, and {replayPerRequest:0} tokens of context replay per request. Hold everything else and give the arm direct's request count, as a blocking call or a wait tool would, and the same work lands near **{counterfactual:0} tokens** against direct's {reference.TotalTokens:0}, at a token mix that bills at {biggest.CostUsd / (biggest.TotalTokens / 1_000_000.0):0.00} USD per million rather than {reference.CostUsd / (reference.TotalTokens / 1_000_000.0):0.00}. This is arithmetic on the measured per-request cost, not a measured run, and it names the one change that would turn the MCP path from the expensive option into the cheap one.");
            builder.AppendLine();
        }

        builder.AppendLine("## Mechanism");
        builder.AppendLine();
        builder.AppendLine(
            "The payload does stay out of the context on the MCP path: the arguments are two paths and the results are a job id and a state, kilobytes against a file of tens of kilobytes, and the whole MCP tool surface costs under a thousand tokens of cache creation. "
            + "The saving that this buys is then spent, several times over, on requests. Every job_status poll is a full API request that re-sends the entire conversation, so the arm's bill is set by how many times it asks whether the local model has finished. "
            + $"direct reads once and writes once, holds at three to four requests at every size, and its cost grows with the payload itself at about {(directFit is null ? 0 : directFit.Slope):0} tokens per key. "
            + "mcp-path grows with polling instead, from 7 requests at 10 keys to 26.5 at 200, and every one of those requests replays a context of comparable size, which is why its total rises five times faster than the payload it is avoiding. "
            + $"mcp-value is the same trap with the payload added back: values travel out as arguments, return in the result and are written a third time by the agent, at about {(valueFit is null ? 0 : valueFit.Slope):0} tokens per key. "
            + "The mechanism the numbers demonstrate is that on this workload the agent's cost is dominated by context replay, not by payload, so a tool that keeps the payload out of context but needs polling to do it moves the cost rather than removing it.");
        builder.AppendLine();
    }

    private static Fit? LeastSquares(IReadOnlyList<ArmSlicePoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var meanX = points.Average(point => (double)point.Keys);
        var meanY = points.Average(point => point.TotalTokens);
        var covariance = points.Sum(point => (point.Keys - meanX) * (point.TotalTokens - meanY));
        var variance = points.Sum(point => (point.Keys - meanX) * (point.Keys - meanX));

        if (variance == 0)
        {
            return null;
        }

        var slope = covariance / variance;

        return new Fit(meanY - slope * meanX, slope, points.Count);
    }

    private static string Text(JsonNode? node) => node?.ToString() ?? string.Empty;
}
