using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Engine.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// What the PowerShell reference answered, as `tests\parity\languages\oracle.ps1`
/// recorded it.
/// </summary>
public sealed record LanguageOracle
{
    [JsonPropertyName("total")] public int Total { get; init; }

    [JsonPropertyName("resolve")] public IReadOnlyList<ResolveCase> Resolve { get; init; } = [];

    [JsonPropertyName("patterns")] public IReadOnlyList<PatternCase> Patterns { get; init; } = [];

    [JsonPropertyName("models")] public IReadOnlyList<ModelCase> Models { get; init; } = [];

    [JsonPropertyName("warnings")] public IReadOnlyList<WarningCase> Warnings { get; init; } = [];

    [JsonPropertyName("prompts")] public IReadOnlyList<PromptCase> Prompts { get; init; } = [];

    [JsonPropertyName("systemPrompts")] public IReadOnlyList<SystemPromptCase> SystemPrompts { get; init; } = [];

    public sealed record SystemPromptCase
    {
        [JsonPropertyName("language")] public string Language { get; init; } = "";

        [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    }

    public sealed record PromptCase
    {
        [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";

        [JsonPropertyName("target")] public string Target { get; init; } = "";

        [JsonPropertyName("hasShape")] public bool HasShape { get; init; }

        [JsonPropertyName("label")] public string? Label { get; init; }

        [JsonPropertyName("role")] public string? Role { get; init; }

        [JsonPropertyName("blankLines")] public int? BlankLines { get; init; }

        [JsonPropertyName("instruction")] public string? Instruction { get; init; }
    }

    public sealed record ResolveCase
    {
        [JsonPropertyName("query")] public string Query { get; init; } = "";

        [JsonPropertyName("code")] public string? Code { get; init; }
    }

    public sealed record PatternCase
    {
        [JsonPropertyName("code")] public string Code { get; init; } = "";

        [JsonPropertyName("native")] public string Native { get; init; } = "";

        [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    }

    public sealed record ModelCase
    {
        [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";

        [JsonPropertyName("flag")] public string Flag { get; init; } = "";

        [JsonPropertyName("verified")] public bool Verified { get; init; }

        [JsonPropertyName("codes")] public IReadOnlyList<string> Codes { get; init; } = [];
    }

    public sealed record WarningCase
    {
        [JsonPropertyName("code")] public string Code { get; init; } = "";

        [JsonPropertyName("modelId")] public string ModelId { get; init; } = "";

        [JsonPropertyName("warning")] public string Warning { get; init; } = "";
    }
}

/// <summary>
/// The C# port of `languages.ps1` against the script itself. Not "the API looks
/// right" -- every one of the 267 recorded answers has to come back the same,
/// because a language registry that quietly disagrees with the reference engine
/// is how a locale file ends up written under the wrong code.
/// </summary>
public sealed class LanguageParityTests(ITestOutputHelper output)
{
    private static readonly LanguageRegistry Registry = new();

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "parity", "languages", "oracle.json");

    private static LanguageOracle Oracle()
    {
        File.Exists(FixturePath).Should().BeTrue(
            $"the parity fixture must be copied to the output: {FixturePath}");

        return BomSafeJson.Deserialize<LanguageOracle>(File.ReadAllBytes(FixturePath))!;
    }

    [Fact]
    public void EveryRowInTheRegistryIsLoaded()
    {
        var oracle = Oracle();

        Registry.All.Should().HaveCount(oracle.Total);
        Registry.All.Should().OnlyContain(l => l.Code.Length > 0 && l.Name.Length > 0);
    }

    [Fact]
    public void TheDataFileCarriesABomAndIsStillRead()
    {
        // Not hypothetical: config\languages.json in the reference engine has a
        // UTF-8 BOM today, and System.Text.Json rejects 0xEF as an invalid start
        // of a value. If this ever regresses the registry loads as empty.
        Registry.All.Should().NotBeEmpty();

        var bomd = "﻿{\"total\":7}"u8.ToArray();
        BomSafeJson.Deserialize<LanguageOracle>(bomd)!.Total.Should().Be(7);
    }

    [Fact]
    public void ResolveAnswersWhatTheScriptAnswers()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Resolve)
        {
            var mine = Registry.Resolve(c.Query)?.Code;

            if (!string.Equals(mine, c.Code, StringComparison.Ordinal))
            {
                mismatches.Add($"'{c.Query}' -> script:{c.Code ?? "null"} port:{mine ?? "null"}");
            }
        }

        output.WriteLine($"{oracle.Resolve.Count} resolve cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches.Take(20))
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void TheUkrainianAliasIsWhyThisRegistryExists()
    {
        // "ua" is the country code, not the language code. A locale file written
        // as ua.json is not found by tooling expecting uk.
        Registry.Resolve("ua")!.Code.Should().Be("uk");
        Registry.Resolve("UA")!.Code.Should().Be("uk");
        Registry.Resolve("uk")!.Code.Should().Be("uk");
        Registry.Resolve("Ukrainian")!.Code.Should().Be("uk");
    }

    [Fact]
    public void ScriptPatternsMatchTheScript()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Patterns)
        {
            var mine = ScriptPattern.For(c.Native);

            if (!string.Equals(mine, c.Pattern, StringComparison.Ordinal))
            {
                mismatches.Add($"{c.Code} ({c.Native}) -> script:'{c.Pattern}' port:'{mine}'");
            }
        }

        output.WriteLine($"{oracle.Patterns.Count} languages, {mismatches.Count} mismatched");
        foreach (var m in mismatches.Take(20))
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void AnAsciiOnlyNativeNameGivesNoSignalRatherThanAWrongOne()
    {
        // Deutsch, Bahasa Indonesia: there is genuinely nothing to check, and a
        // class that matched anything would be worse than no answer.
        var oracle = Oracle();
        var silent = oracle.Patterns.Where(p => p.Pattern.Length == 0).ToList();

        silent.Should().NotBeEmpty("some languages write their own name in ASCII");

        foreach (var c in silent)
        {
            ScriptPattern.For(c.Native).Should().BeEmpty();
        }
    }

    [Fact]
    public void ModelFilteringMatchesTheScript()
    {
        var oracle = Oracle();

        foreach (var c in oracle.Models)
        {
            LanguageRegistry.ModelFlag(c.ModelId).Should().Be(c.Flag, $"flag for '{c.ModelId}'");
            LanguageRegistry.IsVerified(c.ModelId).Should().Be(c.Verified, $"verified for '{c.ModelId}'");

            Registry.ForModel(c.ModelId).Select(l => l.Code)
                .Should().Equal(c.Codes, $"language list for '{c.ModelId}'");
        }
    }

    [Fact]
    public void WarningsAreWordedExactlyAsTheScriptWordsThem()
    {
        var oracle = Oracle();

        foreach (var c in oracle.Warnings)
        {
            var language = Registry.Resolve(c.Code);

            LanguageRegistry.Warning(language, c.ModelId)
                .Should().Be(c.Warning, $"warning for {c.Code} under '{c.ModelId}'");
        }
    }

    [Fact]
    public void TheTrainedPromptShapeMatchesTheScriptWordForWord()
    {
        var oracle = Oracle();
        var prompts = new ModelPrompts();

        oracle.Prompts.Should().NotBeEmpty("the oracle must actually cover this");

        foreach (var c in oracle.Prompts)
        {
            var code = c.Target == "Czech" ? "cs" : "de";
            var mine = prompts.For(c.ModelId, "English", "en", c.Target, code);

            if (!c.HasShape)
            {
                mine.Should().BeNull($"'{c.ModelId}' publishes no trained shape");
                continue;
            }

            mine.Should().NotBeNull($"'{c.ModelId}' publishes a trained shape");
            mine!.Label.Should().Be(c.Label);
            mine.BlankLines.Should().Be(c.BlankLines!.Value);
            mine.Role.ToString().Should().BeEquivalentTo(c.Role);

            // Verbatim. The whole point of the file is that the wording is the
            // model card's, punctuation included -- paraphrasing it throws away
            // the fine-tune just as surely as using a different prompt.
            mine.Instruction.Should().Be(c.Instruction);
        }
    }

    [Fact]
    public void TheEngineSystemPromptMatchesTheScriptWordForWord()
    {
        var oracle = Oracle();
        var fragments = new PromptFragments();

        oracle.SystemPrompts.Should().NotBeEmpty("the oracle lifts it out of console.ps1");

        foreach (var c in oracle.SystemPrompts)
        {
            fragments.SystemPrompt(c.Language).Should().Be(c.Prompt, $"the prompt for {c.Language}");
        }
    }

    [Fact]
    public void TheSystemPromptExplainsWhatAProtectedBlockIs()
    {
        // The clause that makes MarkupGuard's whole mechanism work. The guard
        // replaces fences, comments, URLs and paths with [[n]]; a model never
        // told what those are has no reason to reproduce them, and the chunk
        // then fails its own integrity check for a defect the prompt caused.
        var prompt = new PromptFragments().SystemPrompt("Czech");

        prompt.Should().Contain("[[7]]")
            .And.Contain("protected blocks")
            .And.Contain("never translate or renumber them");

        prompt.Should().Contain("Czech", "the target is substituted, not hardcoded");
        prompt.Should().NotContain("{TARGET}", "the placeholder must not survive into the prompt");
    }

    [Fact]
    public void OnlyAModelTrainedOnAShapeClaimsOne()
    {
        var prompts = new ModelPrompts();

        prompts.HasTrainedShape("translategemma-4b-it.Q4_K_M").Should().BeTrue();

        // EuroLLM backs the Simple tier and publishes no trained prompt, so it is
        // asked in a way it was never tuned for. Pinned because it is the fact
        // the runtime's fixed prompt collides with.
        prompts.HasTrainedShape("EuroLLM-9B-Instruct-Q4_K_M").Should().BeFalse();
        prompts.HasTrainedShape(null).Should().BeFalse();
    }

    /// <summary>
    /// Regenerates the oracle from the reference and fails if it has moved.
    /// Gated: launching PowerShell costs more than the whole default suite, and
    /// the fixture is what the fast tests above compare against.
    /// </summary>
    [ParityFact]
    public void TheFixtureStillMatchesTheReferenceScript()
    {
        ParityOracle.Regenerate("languages", FixturePath, output);
    }
}

/// <summary>
/// Gates a parity test behind BETTERTRANSLATOR_PARITY_TESTS=1. Launching
/// PowerShell costs more than the entire default suite, and xunit 2.x decides
/// Skip at discovery, so the check belongs in the attribute.
/// </summary>
public sealed class ParityFactAttribute : FactAttribute
{
    public const string Gate = "BETTERTRANSLATOR_PARITY_TESTS";

    public ParityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Gate) != "1")
        {
            Skip = $"Set {Gate}=1 to run. Launches PowerShell against the reference engine.";
        }
    }
}
