using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Terminology;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RolloutValidationRun
{
    private const string OutputRelativePath = "artifacts/rollout";

    private const string CorpusRelativePath = "tests/BetterTranslator.Tests/Fixtures/Parallel/en-cs.json";

    private const int Repetitions = 3;

    private static readonly CheckRunSettings EnglishToCzech = new() { SourceLanguage = "en", TargetLanguage = "cs" };

    private sealed record CorpusDocument(string Name, IReadOnlyList<string> Source, IReadOnlyList<string> Target);

    private sealed record InjectedDocument(string Defect, CheckContext Context, IReadOnlyList<string> BuiltFor);

    private sealed record DocumentMetrics(
        string Document,
        double Completion,
        IReadOnlyDictionary<string, int> ResidualByGranularity,
        IReadOnlyDictionary<string, int> FindingsPerCheck,
        int RepairAttempts,
        int RepairAccepts,
        int SpansRed,
        double WallClockMs,
        long PeakWorkingSetBytes,
        int ModelCalls,
        int GeneratedTokens,
        int CharactersChanged);

    private static string Root() => RolloutBaselineSnapshotTests.RepositoryRoot();

    private static string OutputDirectory()
    {
        var path = Path.Combine(Root(), OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<CorpusDocument> LoadCorpus()
    {
        var path = Path.Combine(Root(), CorpusRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var groups = new Dictionary<string, (List<string> Source, List<string> Target)>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var pair in json.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var origin = Path.GetFileNameWithoutExtension(pair.GetProperty("origin").GetString() ?? "unknown");
            var source = pair.GetProperty("source").GetString() ?? string.Empty;
            var target = pair.GetProperty("target").GetString() ?? string.Empty;

            if (source.Contains('\n', StringComparison.Ordinal) || target.Contains('\n', StringComparison.Ordinal))
            {
                continue;
            }

            if (!groups.TryGetValue(origin, out var lines))
            {
                lines = ([], []);
                groups[origin] = lines;
                order.Add(origin);
            }

            lines.Source.Add(source);
            lines.Target.Add(target);
        }

        return [.. order.Select(name => new CorpusDocument(name, groups[name].Source, groups[name].Target))];
    }

    private static (string Source, string Target, List<SegmentTrace> Traces) Lines(IReadOnlyList<string> source, IReadOnlyList<string?> target)
    {
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            var answer = target[i];

            traces.Add(answer is null
                ? new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Dropped)
                : new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Translated, answer, null, answer, targetAt, answer.Length));

            sourceAt += source[i].Length + 1;

            if (answer is not null)
            {
                targetAt += answer.Length + 1;
            }
        }

        return (string.Join('\n', source), string.Join('\n', target.Where(t => t is not null)), traces);
    }

    private static CheckContext Context(IReadOnlyList<string> source, IReadOnlyList<string?> target, IEnumerable<ExemptSpan>? exemptions = null)
    {
        var (sourceText, targetText, traces) = Lines(source, target);
        return StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, exemptions, EnglishToCzech);
    }

    private static CheckContext Context(CorpusDocument document) => Context(document.Source, [.. document.Target]);

    private static GateSettings AllOn() => new();

    private static GateSettings AllOff()
    {
        var settings = new GateSettings { Enabled = false };

        foreach (var category in StageTable.Default.Categories)
        {
            settings.Disable(category);
        }

        return settings;
    }

    private static DocumentMetrics Measure(CorpusDocument document, GateSettings settings)
    {
        var context = Context(document);
        var watch = Stopwatch.StartNew();
        var result = new VerificationGate(CheckRegistry.Default, settings).Run(context);
        watch.Stop();

        var residual = result.Defects
            .GroupBy(d => d.Granularity.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var changed = context.Target.Text.Zip(string.Join('\n', document.Target)).Count(p => p.First != p.Second)
            + Math.Abs(context.Target.Text.Length - string.Join('\n', document.Target).Length);

        return new DocumentMetrics(
            document.Name,
            result.CompletionPercent,
            residual,
            result.FindingCounts,
            0,
            0,
            result.Defects.Count,
            watch.Elapsed.TotalMilliseconds,
            Process.GetCurrentProcess().PeakWorkingSet64,
            0,
            0,
            changed);
    }

    private static string RenderRun(IReadOnlyList<DocumentMetrics> metrics)
    {
        var builder = new StringBuilder();

        foreach (var m in metrics)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{m.Document}|completion={m.Completion:0.0}|residual={Join(m.ResidualByGranularity)}|findings={Join(m.FindingsPerCheck)}|repairAttempts={m.RepairAttempts}|repairAccepts={m.RepairAccepts}|red={m.SpansRed}|ms={m.WallClockMs:0.000}|peakWS={m.PeakWorkingSetBytes}|modelCalls={m.ModelCalls}|tokens={m.GeneratedTokens}|charsChanged={m.CharactersChanged}");
        }

        return builder.ToString();
    }

    private static string Join(IReadOnlyDictionary<string, int> values) =>
        values.Count == 0 ? "none" : string.Join(",", values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value.ToString(CultureInfo.InvariantCulture)));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    private static string Spread(IEnumerable<double> values)
    {
        var list = values.ToList();
        var median = Median(list);
        var spread = median == 0 ? 0 : (list.Max() - list.Min()) / median * 100.0;
        return spread > 10 ? string.Create(CultureInfo.InvariantCulture, $" (spread {spread:0}%)") : string.Empty;
    }

    private static string RenderBeforeAfter(IReadOnlyList<IReadOnlyList<DocumentMetrics>> baseline, IReadOnlyList<IReadOnlyList<DocumentMetrics>> current)
    {
        var builder = new StringBuilder();
        builder.AppendLine("| document | completion before | completion after | residual after (granularity) | findings after (check) | repair attempts / accepts / red | ms before | ms after | peak WS MB before | peak WS MB after | model calls / tokens | chars changed |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        var count = current[0].Count;

        for (var i = 0; i < count; i++)
        {
            var b = baseline.Select(run => run[i]).ToList();
            var c = current.Select(run => run[i]).ToList();
            builder.AppendLine(Row(c[0].Document, b, c));
        }

        builder.AppendLine(Row("all documents", [.. baseline.Select(Summary)], [.. current.Select(Summary)]));
        return builder.ToString();
    }

    private static DocumentMetrics Summary(IReadOnlyList<DocumentMetrics> run) =>
        new(
            "all documents",
            Math.Round(run.Average(m => m.Completion), 1, MidpointRounding.AwayFromZero),
            run.SelectMany(m => m.ResidualByGranularity).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(p => p.Value), StringComparer.Ordinal),
            run.SelectMany(m => m.FindingsPerCheck).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(p => p.Value), StringComparer.Ordinal),
            run.Sum(m => m.RepairAttempts),
            run.Sum(m => m.RepairAccepts),
            run.Sum(m => m.SpansRed),
            run.Sum(m => m.WallClockMs),
            run.Max(m => m.PeakWorkingSetBytes),
            run.Sum(m => m.ModelCalls),
            run.Sum(m => m.GeneratedTokens),
            run.Sum(m => m.CharactersChanged));

    private static string Row(string name, IReadOnlyList<DocumentMetrics> b, IReadOnlyList<DocumentMetrics> c)
    {
        var culture = CultureInfo.InvariantCulture;
        var msBefore = Median(b.Select(m => m.WallClockMs));
        var msAfter = Median(c.Select(m => m.WallClockMs));
        var wsBefore = Median(b.Select(m => m.PeakWorkingSetBytes / 1048576.0));
        var wsAfter = Median(c.Select(m => m.PeakWorkingSetBytes / 1048576.0));

        return string.Create(
            culture,
            $"| {name} | {Median(b.Select(m => m.Completion)):0.0} | {Median(c.Select(m => m.Completion)):0.0} | {Join(c[0].ResidualByGranularity)} | {Join(c[0].FindingsPerCheck)} | {c[0].RepairAttempts} / {c[0].RepairAccepts} / {c[0].SpansRed} | {msBefore:0.00}{Spread(b.Select(m => m.WallClockMs))} | {msAfter:0.00}{Spread(c.Select(m => m.WallClockMs))} | {wsBefore:0.0} | {wsAfter:0.0} | {c[0].ModelCalls} / {c[0].GeneratedTokens} | {c[0].CharactersChanged} |");
    }

    [Fact]
    public void Corpus_runs_three_times_per_configuration_and_writes_metrics()
    {
        var corpus = LoadCorpus();
        corpus.Should().NotBeEmpty();
        var output = OutputDirectory();

        CheckInstrumentation.Reset();
        var armed = CheckInstrumentation.Armed;

        var current = new List<IReadOnlyList<DocumentMetrics>>();
        var baseline = new List<IReadOnlyList<DocumentMetrics>>();

        for (var run = 1; run <= Repetitions; run++)
        {
            var currentRun = corpus.Select(d => Measure(d, AllOn())).ToList();
            current.Add(currentRun);
            File.WriteAllText(Path.Combine(output, $"metrics-current-run{run}.txt"), RenderRun(currentRun), new UTF8Encoding(false));
        }

        for (var run = 1; run <= Repetitions; run++)
        {
            var baselineRun = corpus.Select(d => Measure(d, AllOff())).ToList();
            baseline.Add(baselineRun);
            File.WriteAllText(Path.Combine(output, $"metrics-baseline-run{run}.txt"), RenderRun(baselineRun), new UTF8Encoding(false));
        }

        var counts = CheckInstrumentation.Render();
        File.WriteAllText(Path.Combine(output, armed ? "instrumentation-counts.txt" : "instrumentation-counts-unarmed.txt"), "armed " + armed + "\n" + counts, new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(output, "before-after.md"), RenderBeforeAfter(baseline, current), new UTF8Encoding(false));

        var guard = new StringBuilder();

        foreach (var document in corpus)
        {
            var baselineCompletion = Median(baseline.Select(run => run.Single(m => m.Document == document.Name).Completion));
            var changed = current.Max(run => run.Single(m => m.Document == document.Name).CharactersChanged);
            guard.AppendLine(CultureInfo.InvariantCulture, $"{document.Name}|baselineCompletion={baselineCompletion:0.0}|charactersChanged={changed}|{(baselineCompletion >= 100.0 && changed == 0 ? "byte-identical" : baselineCompletion < 100.0 ? "not-clean-at-baseline" : "REGRESSION")}");
        }

        File.WriteAllText(Path.Combine(output, "regression-guard.txt"), guard.ToString(), new UTF8Encoding(false));

        var report = new VerificationGate(CheckRegistry.Default, AllOn()).Run(Context(corpus[0]));
        GateRunReport.Write(report, output, "gate-run-corpus.log");

        current.Should().OnlyContain(run => run.All(m => m.CharactersChanged == 0));
        baseline.Should().OnlyContain(run => run.All(m => m.FindingsPerCheck.Count == 0));
    }

    private static string Slice(CorpusDocument document, CheckRange range)
    {
        var text = string.Join('\n', document.Target);
        var offset = Math.Clamp(range.Offset, 0, text.Length);
        return text.Substring(offset, Math.Clamp(range.Length, 0, text.Length - offset)).Replace('\n', ' ');
    }

    private static CheckContext Masked(string source, string answer, string spliced, MaskTrace mask)
    {
        var trace = new SegmentTrace(0, source.Length, SegmentOutcome.Translated, answer, [mask], spliced, 0, spliced.Length);
        return StructureContext.Build(source, spliced, ProseStructure.Instance, [trace], settings: EnglishToCzech);
    }

    private static TableLemmatizer Czech() =>
        new TableLemmatizer("cs")
            .Add("složka", "složku", "složky", "složce")
            .Add("adresář", "adresáře", "adresáři")
            .Add("ustanovení", "ustanovením")
            .Add("opatření", "opatřením")
            .Add("zajištění", "zajištěním")
            .Add("uložit", "uložte")
            .Add("soubor", "soubory", "souboru");

    private static IReadOnlyList<InjectedDocument> Injections(CorpusDocument clean)
    {
        var source = clean.Source.ToList();
        var target = clean.Target.ToList();
        var list = new List<InjectedDocument>();

        var dropped = target.Select(t => (string?)t).ToList();
        dropped[1] = null;
        list.Add(new InjectedDocument("dropped chunk", Context(source, dropped), [CheckId.Structure.ChunkParity, CheckId.Coverage.DroppedUnit]));

        var untranslated = target.ToList();
        untranslated[1] = source[1];
        list.Add(new InjectedDocument("untranslated chunk", Context(source, [.. untranslated]), [CheckId.Coverage.CopyThrough]));

        var surviving = target.ToList();
        var sourceWord = source[0].Split(' ').OrderByDescending(w => w.Length).First().Trim('.', ',');
        var targetWords = surviving[0].Split(' ');
        targetWords[Math.Min(1, targetWords.Length - 1)] = sourceWord;
        surviving[0] = string.Join(' ', targetWords);
        list.Add(new InjectedDocument("surviving source word", Context(source, [.. surviving]), [CheckId.Coverage.SourceTokenSurvival]));

        list.Add(new InjectedDocument(
            "word fused with a protected name",
            Masked("Open BetterTranslator now.", "Otevřete[[0]] nyní.", "OtevřeteBetterTranslator nyní.", new MaskTrace("[[0]]", "BetterTranslator", ExemptionReason.ProtectedName)),
            [CheckId.Structure.BoundaryIntegrity]));

        list.Add(new InjectedDocument(
            "lost mask placeholder",
            Masked("Press {0} to save the file.", "Stiskněte pro uložení souboru.", "Stiskněte pro uložení souboru.", new MaskTrace("[[0]]", "{0}", ExemptionReason.FormatPlaceholder)),
            [CheckId.Structure.PlaceholderDefect, CheckId.Structure.PlaceholderCensus, CheckId.Structure.InvariantMultiset]));

        var residueTarget = target.ToList();
        residueTarget[0] = "Stiskněte [[0]] pro uložení souboru.";
        var residueSource = source.ToList();
        residueSource[0] = "Press {0} to save the file.";
        list.Add(new InjectedDocument("sentinel residue in target", Context(residueSource, [.. residueTarget]), [CheckId.Structure.PlaceholderCensus]));

        const string subtitle = "1\n00:00:01,000 --> 00:00:03,250\nOpen the settings panel.\n\n2\n00:00:03,400 --> 00:00:06,000\nPin the sidebar to your workspace.\n\n3\n00:00:06,100 --> 00:00:08,000\nDone.\n";
        var subtitleTarget = subtitle
            .Replace("Open the settings panel.", "Otevřete panel nastavení.", StringComparison.Ordinal)
            .Replace("Pin the sidebar to your workspace.", "Připněte postranní panel.", StringComparison.Ordinal)
            .Replace("Done.", "Hotovo.", StringComparison.Ordinal)
            .Replace("00:00:06,000", "00:00:06,001", StringComparison.Ordinal);
        list.Add(new InjectedDocument("drifted subtitle timecode", StructureContext.Build(subtitle, subtitleTarget, settings: EnglishToCzech), [CheckId.Structure.SubtitleParity]));

        var repetition = target.ToList();
        repetition[0] = target[0].TrimEnd('.') + string.Concat(Enumerable.Repeat(" a znovu", 12)) + ".";
        list.Add(new InjectedDocument("repetition loop", Context(source, [.. repetition]), [CheckId.Ratio.Repetition, CheckId.Ratio.LengthRatio]));

        var truncated = target.ToList();
        var longest = Enumerable.Range(0, target.Count).OrderByDescending(i => target[i].Length).First();
        truncated[longest] = target[longest][..Math.Max(3, target[longest].Length / 3)];
        list.Add(new InjectedDocument("truncated segment", Context(source, [.. truncated]), [CheckId.Ratio.Truncation, CheckId.Ratio.LengthRatio]));

        var preamble = target.ToList();
        preamble[0] = "Zde je překlad, který jste požadovali, přeložený do češtiny: " + target[0];
        list.Add(new InjectedDocument("leaked preamble", Context(source, [.. preamble]), [CheckId.Ratio.Insertion, CheckId.Ratio.LengthRatio]));

        var rejectedSource = new[] { "Save it to the folder.", "Open the folder again." };
        var rejectedTarget = new[] { "Uložte to do adresáře.", "Otevřete složku znovu." };
        var rejected = Context(rejectedSource, [.. rejectedTarget]);
        TerminologyPorts.Attach(rejected, new TerminologyServices(Czech(), new TerminologyGlossary().Add("folder", "složka", "adresář")));
        list.Add(new InjectedDocument("rejected glossary rendering", rejected, [CheckId.Terminology.RejectedRendering]));

        var lemmaSource = new[] { "The provision applies to every contract.", "Read the provision before signing.", "This provision is optional." };
        var lemmaTarget = new[] { "Ustanovení platí pro každou smlouvu.", "Před podpisem si přečtěte opatření.", "Toto zajištění je nepovinné." };
        var lemmas = Context(lemmaSource, [.. lemmaTarget]);
        TerminologyPorts.Attach(lemmas, new TerminologyServices(Czech(), new TerminologyGlossary().Add("provision", "ustanovení", "opatření", "zajištění")));
        list.Add(new InjectedDocument("one term under several lemmas", lemmas, [CheckId.Terminology.RunConsistency]));

        return list;
    }

    [Fact]
    public void Injected_defects_report_detection_and_false_positive_rates_per_check()
    {
        var corpus = LoadCorpus();
        var output = OutputDirectory();
        var checkIds = CheckRegistry.Default.Checks.Select(c => c.CheckId).Where(id => !id.StartsWith("GATE", StringComparison.Ordinal)).ToList();

        var cleanHits = new Dictionary<string, int>(StringComparer.Ordinal);
        var evidence = new StringBuilder();

        foreach (var document in corpus)
        {
            var result = new VerificationGate(CheckRegistry.Default, AllOn()).Run(Context(document));

            foreach (var routed in result.Routed)
            {
                evidence.AppendLine(CultureInfo.InvariantCulture, $"clean|{document.Name}|{routed.CheckId}|{routed.Severity}|{routed.Action}|{routed.TargetRange.UnitPath}+{routed.TargetRange.Length}|{Slice(document, routed.TargetRange)}|{routed.Finding.Evidence}");
            }

            foreach (var id in result.FindingCounts.Where(p => p.Value > 0).Select(p => p.Key))
            {
                cleanHits[id] = cleanHits.GetValueOrDefault(id) + 1;
            }
        }

        var clean = corpus.OrderByDescending(d => d.Source.Count).First();
        var cleanResult = new VerificationGate(CheckRegistry.Default, AllOn()).Run(Context(clean));
        var cleanFired = cleanResult.FindingCounts.Where(p => p.Value > 0).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var cleanResultSource = string.Join('\n', clean.Source);

        var builder = new StringBuilder();
        builder.AppendLine("base document " + clean.Name + " lines " + clean.Source.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("clean fired " + (cleanFired.Count == 0 ? "none" : string.Join(",", cleanFired.Order(StringComparer.Ordinal))));

        var detectedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var builtFor = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var injection in Injections(clean))
        {
            var result = new VerificationGate(CheckRegistry.Default, AllOn()).Run(injection.Context);
            var fired = result.FindingCounts
                .Where(p => p.Value > (injection.Context.Source.Text == cleanResultSource ? cleanResult.FindingCounts.GetValueOrDefault(p.Key) : 0))
                .Select(p => p.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            var routed = result.Routed.Select(r => r.CheckId + "/" + r.Action + "/" + r.Severity).Distinct().Order(StringComparer.Ordinal).ToList();

            foreach (var r in result.Routed)
            {
                evidence.AppendLine(CultureInfo.InvariantCulture, $"injected|{injection.Defect}|{r.CheckId}|{r.Severity}|{r.Action}|{r.TargetRange.UnitPath}+{r.TargetRange.Length}|{r.Finding.Evidence}");
            }
            builder.AppendLine(CultureInfo.InvariantCulture, $"defect [{injection.Defect}] builtFor={string.Join(",", injection.BuiltFor)} fired={(fired.Count == 0 ? "none" : string.Join(",", fired))} routed={(routed.Count == 0 ? "none" : string.Join(",", routed))}");

            foreach (var id in injection.BuiltFor)
            {
                if (!builtFor.TryGetValue(id, out var list))
                {
                    list = [];
                    builtFor[id] = list;
                }

                list.Add(injection.Defect);
            }

            foreach (var id in fired)
            {
                if (!detectedBy.TryGetValue(id, out var list))
                {
                    list = [];
                    detectedBy[id] = list;
                }

                list.Add(injection.Defect);
            }
        }

        builder.AppendLine("per-check");

        foreach (var id in checkIds)
        {
            var expected = builtFor.GetValueOrDefault(id) ?? [];
            var detected = detectedBy.GetValueOrDefault(id) ?? [];
            var hit = expected.Count(defect => detected.Contains(defect));
            var rate = expected.Count == 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{hit}/{expected.Count}");
            var extra = detected.Where(d => !expected.Contains(d)).ToList();
            builder.AppendLine(CultureInfo.InvariantCulture, $"{id}|detection={rate}|falsePositiveDocs={cleanHits.GetValueOrDefault(id)}/{corpus.Count}|alsoFiredOn={(extra.Count == 0 ? "none" : string.Join(";", extra))}");
        }

        File.WriteAllText(Path.Combine(output, "detection.txt"), builder.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "findings-evidence.txt"), evidence.ToString(), new UTF8Encoding(false));
        var armedNow = CheckInstrumentation.Armed;
        File.WriteAllText(Path.Combine(output, armedNow ? "instrumentation-counts-with-injection.txt" : "instrumentation-counts-with-injection-unarmed.txt"), "armed " + armedNow + "\n" + CheckInstrumentation.Render(), new UTF8Encoding(false));

        detectedBy.Should().NotBeEmpty();
    }
}
