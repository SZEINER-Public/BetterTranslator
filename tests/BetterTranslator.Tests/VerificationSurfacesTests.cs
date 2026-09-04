using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Cli;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Structure;
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Agents.Mcp;
using BetterTranslator.Runtime.Downloads;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class VerificationSurfacesTests : IDisposable
{
    private static readonly string[] Source = ["Open the settings window.", "Save the file."];

    private static readonly string[] Dropped = ["Otevřete okno nastavení."];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-surfaces", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static IReadOnlyList<SegmentTrace> Traces(IReadOnlyList<string> source, IReadOnlyList<string?> target)
    {
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            var answer = i < target.Count ? target[i] : null;
            traces.Add(answer is null
                ? new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Dropped)
                : new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Translated, answer, null, answer, targetAt, answer.Length));
            sourceAt += source[i].Length + 1;
            targetAt += answer is null ? 0 : answer.Length + 1;
        }

        return traces;
    }

    private static Core.Verification.VerificationResult DroppedUnitResult() =>
        new VerificationPipeline(null, new VerificationSettings()).Verify(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en", "cs");

    [Fact]
    public void The_summary_carries_completion_red_spans_and_skipped_checks_from_the_gate()
    {
        var result = DroppedUnitResult();

        var summary = VerificationSummary.From(result);

        summary.Available.Should().BeTrue();
        summary.Completion.Should().Be(50.0);
        summary.CompletionText.Should().Be("50.0");
        summary.Defects.Should().Be(result.Gate!.Defects.Count);
        summary.RedSpans.Should().Contain(s => s.CheckIds.Contains(CheckId.Coverage.DroppedUnit));
        summary.RepairCandidates.Should().Be(result.Gate.RepairCandidates.Count);
        summary.Skipped.Should().NotBeNull();
    }

    [Fact]
    public void The_summary_says_why_when_nothing_ran()
    {
        VerificationSummary.From(null).Should().BeEquivalentTo(VerificationSummary.Unavailable(VerificationSummary.NoPipelineReason));

        var off = new VerificationSettings();
        off.Gate.Enabled = false;
        var result = new VerificationPipeline(null, off).Verify(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en", "cs");

        var summary = VerificationSummary.From(result);

        summary.Available.Should().BeFalse();
        summary.Reason.Should().Contain("disabled");

        new EntrySummary("id", "chat", "Sentence", "Done", "s", "r", "cs", "now", null, null, null).Verification.Reason.Should().Be(VerificationSummary.NotRetainedReason);
    }

    [Fact]
    public void The_gateway_summary_matches_the_chat_path_for_the_same_input()
    {
        var pipeline = new VerificationPipeline(null, new VerificationSettings());
        var chat = pipeline.Verify(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en", "cs");
        var gateway = pipeline.Verify(string.Join('\n', Source), string.Join('\n', Dropped), Traces(Source, Dropped), "en", "cs");

        VerificationSummary.From(gateway).Should().BeEquivalentTo(VerificationSummary.From(chat));
        gateway.Gate!.FindingCounts.Should().Equal(chat.Gate!.FindingCounts);
    }

    [Fact]
    public void The_cli_envelope_carries_the_object_only_when_asked()
    {
        var translation = new TextTranslation("en", "cs", "EuroLLM", "Otevřete okno nastavení.", 7, 120, null) { Verification = VerificationSummary.From(DroppedUnitResult()) };

        using var plain = JsonDocument.Parse(Envelope.Text(translation));
        plain.RootElement.TryGetProperty("verification", out _).Should().BeFalse();
        plain.RootElement.GetProperty("result").GetString().Should().Be("Otevřete okno nastavení.");

        using var verified = JsonDocument.Parse(Envelope.Text(translation, verify: true));
        var verification = verified.RootElement.GetProperty("verification");
        verification.GetProperty("available").GetBoolean().Should().BeTrue();
        verification.GetProperty("completion").GetDouble().Should().Be(50.0);
        verification.GetProperty("red_spans").GetArrayLength().Should().BeGreaterThan(0);

        var file = FileTranslation.Done("a.md", "a.cs.md") with { Verification = VerificationSummary.Unavailable("fixture") };
        using var files = JsonDocument.Parse(Envelope.Files([file]));
        files.RootElement.GetProperty("results")[0].TryGetProperty("verification", out _).Should().BeFalse();
        using var filesVerified = JsonDocument.Parse(Envelope.Files([file], verify: true));
        filesVerified.RootElement.GetProperty("results")[0].GetProperty("verification").GetProperty("reason").GetString().Should().Be("fixture");
    }

    [Fact]
    public void The_command_line_parses_the_verify_flag_and_the_help_names_it()
    {
        CommandLine.Parse(["translate", "Hello", "--to", "cs", "--json", "--verify"]).Verify.Should().BeTrue();
        CommandLine.Parse(["translate", "Hello", "--to", "cs"]).Verify.Should().BeFalse();
        Help.For(Verb.Help).Should().Contain("--verify");
    }

    [Fact]
    public void The_tool_schemas_carry_the_verification_object_and_the_tool_names_are_unchanged()
    {
        var schema = McpPayload.Schema("{ \"type\": \"object\", \"properties\": { \"verification\": " + McpPayload.VerificationPlaceholder + " } }");

        schema.GetProperty("properties").GetProperty("verification").GetProperty("properties").GetProperty("red_spans").GetProperty("type").GetString().Should().Be("array");
        BetterTranslatorTools.Names.Should().HaveCount(10);
        BetterTranslatorTools.Names.Should().Equal("translate_text", "translate_file", "translate_batch", "job_status", "job_cancel", "list_languages", "list_models", "select_model", "show_in_gui", "get_entry");
    }

    [Fact]
    public async Task A_gateway_uses_the_pipeline_it_is_given_and_builds_its_own_otherwise()
    {
        Directory.CreateDirectory(_root);
        var paths = new AppPaths(_root);
        var database = new Database(paths);
        await database.MigrateAsync(CancellationToken.None);
        var settings = new AppSettings();
        var shared = new VerificationPipeline(null, settings.Verification);

        using var given = new TranslationGateway(database, new SettingsStore(database), new InstallPaths(paths), settings, new StubEngine(_ => new EngineAnswer("x", null, 1, TimeSpan.Zero)), shared);
        using var own = new TranslationGateway(database, new SettingsStore(database), new InstallPaths(paths), settings, new StubEngine(_ => new EngineAnswer("x", null, 1, TimeSpan.Zero)));

        given.Pipeline.Should().BeSameAs(shared);
        own.Pipeline.Should().NotBeSameAs(shared);
    }
}
