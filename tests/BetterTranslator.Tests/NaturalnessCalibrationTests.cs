using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Naturalness;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Naturalness;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public static class NaturalnessCorpus
{
    public const string WriteProfileVariable = "BT_WRITE_NATURALNESS_PROFILE";

    public const string ProfileRelativePath = "src/BetterTranslator.Core/Verification/Checks/Naturalness/naturalness-profile.json";

    public static IReadOnlyList<NaturalnessSample> Load()
    {
        var path = Path.Combine(RolloutBaselineSnapshotTests.RepositoryRoot(), "tests", "BetterTranslator.Tests", "Fixtures", "Parallel", "en-cs.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        return
        [
            .. json.RootElement.GetProperty("pairs").EnumerateArray()
                .Select(p => new NaturalnessSample("en", "cs", p.GetProperty("source").GetString() ?? string.Empty, p.GetProperty("target").GetString() ?? string.Empty))
                .Where(s => s.Source.Length > 0 && s.Target.Length > 0),
        ];
    }

    public static CheckContext Context(string source, string target)
    {
        var trace = new SegmentTrace(0, source.Length, SegmentOutcome.Translated, target, null, target, 0, target.Length);
        return StructureContext.Build(source, target, ProseStructure.Instance, [trace], settings: new CheckRunSettings { SourceLanguage = "en", TargetLanguage = "cs" });
    }

    public static NaturalnessProfile Calibrate() =>
        NaturalnessCalibration.Calibrate(Load(), Context, new UnavailableTagger("cs", "majka is not installed on the calibration machine"), NaturalnessPackLoader.ShippedPacks["cs"].Pack);
}

public sealed class NaturalnessCalibrationTests
{
    [Fact]
    public void Every_band_carries_its_sample_count_and_no_check_holds_a_threshold_literal()
    {
        var profile = NaturalnessCorpus.Calibrate();

        profile.Pairs.Should().NotBeEmpty();
        profile.Pairs.Should().OnlyContain(p => p.Crossing.Samples >= 0 && p.SentenceExcess.Samples > 0);
        profile.Pairs.Where(p => p.Unit == NaturalnessProfile.UnitSentence).Should().ContainSingle(p => p.Pair == "en-cs");

        var checks = Directory.GetFiles(Path.Combine(RolloutBaselineSnapshotTests.RepositoryRoot(), "src", "BetterTranslator.Core", "Verification", "Checks", "Naturalness"), "*Checks.cs");

        foreach (var file in checks)
        {
            var text = File.ReadAllText(file);
            System.Text.RegularExpressions.Regex.IsMatch(text, @"[<>]=?\s*0\.[0-9]+").Should().BeFalse(Path.GetFileName(file) + " compares against a literal");
        }
    }

    [Fact]
    public void The_shipped_profile_matches_the_committed_corpus_or_is_rewritten_on_request()
    {
        var calibrated = NaturalnessCorpus.Calibrate();
        var path = Path.Combine(RolloutBaselineSnapshotTests.RepositoryRoot(), NaturalnessCorpus.ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (string.Equals(Environment.GetEnvironmentVariable(NaturalnessCorpus.WriteProfileVariable), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(path, calibrated.Render() + Environment.NewLine, new UTF8Encoding(false));
        }

        var shipped = NaturalnessProfile.Parse(File.ReadAllText(path));

        shipped.Pairs.Select(p => (p.Pair, p.Unit, p.Crossing.Limit, p.Crossing.Samples, p.SentenceExcess.Limit, p.SentenceExcess.Samples))
            .Should().BeEquivalentTo(calibrated.Pairs.Select(p => (p.Pair, p.Unit, p.Crossing.Limit, p.Crossing.Samples, p.SentenceExcess.Limit, p.SentenceExcess.Samples)),
                "set " + NaturalnessCorpus.WriteProfileVariable + "=1 and rerun to regenerate the profile");
    }

    [Fact]
    public void Tag_divergence_stays_undetermined_until_a_tagger_covers_the_corpus()
    {
        var profile = NaturalnessCorpus.Calibrate();

        foreach (var pair in profile.Pairs)
        {
            if (!pair.TagDivergence.Calibrated)
            {
                pair.TagReference.Should().BeEmpty(pair.Pair + " " + pair.Unit);
            }
        }
    }
}

public sealed class NaturalnessMeasurementRun
{
    private const int Repetitions = 3;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }

    [Fact]
    public async System.Threading.Tasks.Task Measures_the_pass_over_the_fixture_corpus_off_and_on()
    {
        var samples = NaturalnessCorpus.Load();
        var documents = samples.Select((s, i) => (Name: "pair" + i.ToString("00", CultureInfo.InvariantCulture), s.Source, s.Target)).ToList();
        var output = Path.Combine(RolloutBaselineSnapshotTests.RepositoryRoot(), "artifacts", "naturalness");
        Directory.CreateDirectory(output);

        var rows = new List<string>();
        var perCheck = new Dictionary<string, int>(StringComparer.Ordinal);
        var evidence = new StringBuilder();
        var summary = new Dictionary<string, (List<double> Ms, int Findings, int Requested, int Accepted, Dictionary<string, int> Rejected, int Tokens)>(StringComparer.Ordinal);

        foreach (var (name, enabled) in new[] { ("off", false), ("on", true) })
        {
            var settings = new VerificationSettings { Naturalness = new NaturalnessSettings { RewriteEnabled = enabled } };
            var pipeline = new VerificationPipeline(null, settings) { Naturalness = _ => NaturalnessServices.Shipped(NaturalnessFixtures.CzechTagger()) };
            var pass = new RewritePass(pipeline, settings.Naturalness, (_, _) => System.Threading.Tasks.Task.FromResult(new RewriteAnswer(null, 0)));
            var ms = new List<double>();
            var findings = 0;
            var requested = 0;
            var accepted = 0;
            var tokens = 0;
            var rejected = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var run = 0; run < Repetitions; run++)
            {
                var runFindings = 0;
                var runRequested = 0;
                var runAccepted = 0;
                var runTokens = 0;
                var total = 0.0;

                foreach (var document in documents)
                {
                    var trace = new SegmentTrace(0, document.Source.Length, SegmentOutcome.Translated, document.Target, null, document.Target, 0, document.Target.Length);
                    var watch = Stopwatch.StartNew();
                    var outcome = await pass.RunAsync(document.Source, document.Target, [trace], "en", "cs", enabled ? RepairAutonomy.AutoRepair : RepairAutonomy.Off);
                    watch.Stop();
                    total += watch.Elapsed.TotalMilliseconds;

                    foreach (var routed in outcome.Final.Routed.Where(r => r.Category == CheckId.Naturalness.Category))
                    {
                        runFindings++;

                        if (run == 0 && enabled)
                        {
                            evidence.AppendLine(document.Name + "|" + routed.CheckId + "|" + routed.Finding.Evidence + "|" + document.Target);
                        }

                        if (run == 0 && enabled)
                        {
                            perCheck[routed.CheckId] = perCheck.GetValueOrDefault(routed.CheckId) + 1;
                        }
                    }

                    runRequested += outcome.Requested;
                    runAccepted += outcome.Accepted;
                    runTokens += outcome.GeneratedTokens;

                    foreach (var (reason, count) in outcome.RejectedByReason)
                    {
                        rejected[reason] = rejected.GetValueOrDefault(reason) + count;
                    }
                }

                ms.Add(total);
                findings = runFindings;
                requested = runRequested;
                accepted = runAccepted;
                tokens = runTokens;
            }

            summary[name] = (ms, findings, requested, accepted, rejected, tokens);
        }

        var builder = new StringBuilder();
        builder.AppendLine("| configuration | naturalness findings | candidates requested | candidates accepted | rejected by reason | corpus wall clock ms (median of 3) | added ms per document | generated tokens |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        var offMedian = Median(summary["off"].Ms);

        foreach (var (name, data) in summary)
        {
            var median = Median(data.Ms);
            var added = name == "off" ? 0 : (median - offMedian) / documents.Count;
            var reasons = data.Rejected.Count == 0 ? "none" : string.Join("; ", data.Rejected.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + " " + p.Value));
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {data.Findings} | {data.Requested} | {data.Accepted} | {reasons} | {median:0.00} | {added:0.000} | {data.Tokens} |"));
        }

        builder.AppendLine();
        builder.AppendLine("| check | findings on the corpus (feature on) |");
        builder.AppendLine("|---|---|");

        foreach (var id in CheckRegistry.Default.ForCategory(CheckId.Naturalness.Category).Select(c => c.CheckId))
        {
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {id} | {perCheck.GetValueOrDefault(id)} |"));
        }

        builder.AppendLine();
        builder.AppendLine("Model calls: none. No translation model is installed on the measurement machine, so every candidate request returned nothing and no rewrite was accepted; the added time is the check layer and the accept gate scaffolding only.");
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Documents: {documents.Count} parallel pairs from tests/BetterTranslator.Tests/Fixtures/Parallel/en-cs.json, runs per configuration: {Repetitions}."));

        File.WriteAllText(Path.Combine(output, "measurement.md"), builder.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "findings.txt"), evidence.ToString(), new UTF8Encoding(false));

        summary["on"].Accepted.Should().Be(0);
    }
}
