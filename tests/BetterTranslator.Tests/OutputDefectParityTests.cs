using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>The output-defect half of what `markup-guard.ps1` decided.</summary>
public sealed record DefectOracle
{
    [JsonPropertyName("listing")] public IReadOnlyList<ListingCase> Listing { get; init; } = [];

    [JsonPropertyName("residue")] public IReadOnlyList<ResidueCase> Residue { get; init; } = [];

    [JsonPropertyName("defects")] public IReadOnlyList<DefectCase> Defects { get; init; } = [];

    [JsonPropertyName("leaks")] public IReadOnlyList<LeakCase> Leaks { get; init; } = [];

    [JsonPropertyName("structure")] public IReadOnlyList<StructureCase> Structure { get; init; } = [];

    public sealed record ListingCase
    {
        [JsonPropertyName("line")] public string Line { get; init; } = "";

        [JsonPropertyName("isListing")] public bool IsListing { get; init; }
    }

    public sealed record ResidueRunCase
    {
        [JsonPropertyName("text")] public string Text { get; init; } = "";

        [JsonPropertyName("words")] public int Words { get; init; }
    }

    public sealed record ResidueCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("output")] public string Output { get; init; } = "";

        [JsonPropertyName("protect")] public string Protect { get; init; } = "";

        [JsonPropertyName("runs")] public IReadOnlyList<ResidueRunCase> Runs { get; init; } = [];

        [JsonPropertyName("gate")] public string Gate { get; init; } = "";
    }

    public sealed record DefectItem
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";

        [JsonPropertyName("text")] public string Text { get; init; } = "";

        [JsonPropertyName("words")] public int Words { get; init; }

        [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";

        [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    }

    public sealed record DefectCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("output")] public string Output { get; init; } = "";

        [JsonPropertyName("dnt")] public IReadOnlyList<string> DoNotTranslate { get; init; } = [];

        [JsonPropertyName("items")] public IReadOnlyList<DefectItem> Items { get; init; } = [];
    }

    public sealed record LeakCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("output")] public string Output { get; init; } = "";

        [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";

        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    public sealed record StructureCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("output")] public string Output { get; init; } = "";

        [JsonPropertyName("issues")] public IReadOnlyList<string> Issues { get; init; } = [];
    }
}

/// <summary>
/// The output-defect half of `markup-guard.ps1`, against the script itself.
///
/// These are the seven functions the module was missing, and between them they
/// are what stops a translation that is structurally perfect and wrong from
/// shipping: a half-translated clause, a glued product name, an answer that
/// narrates the task, and a fence closed in the wrong chunk.
/// </summary>
public sealed class OutputDefectParityTests(ITestOutputHelper output)
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "parity", "markup", "oracle.json");

    private static DefectOracle Oracle() =>
        BomSafeJson.Deserialize<DefectOracle>(File.ReadAllBytes(FixturePath))!;

    [Fact]
    public void ListingLinesAreRecognisedExactlyAsTheScriptDoes()
    {
        var oracle = Oracle();
        oracle.Listing.Should().NotBeEmpty();

        foreach (var c in oracle.Listing)
        {
            SourceResidue.IsListingLine(c.Line)
                .Should().Be(c.IsListing, $"'{c.Line}'");
        }
    }

    [Fact]
    public void SurvivingRunsMatchTheScript()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Residue)
        {
            var mine = SourceResidue.Find(c.Source, c.Output, 3, c.Protect);

            var theirs = c.Runs.Select(r => $"{r.Words}:{r.Text}").ToList();
            var ours = mine.Select(r => $"{r.Words}:{r.Text}").ToList();

            if (!theirs.SequenceEqual(ours, StringComparer.Ordinal))
            {
                mismatches.Add($"[{c.Name}] script [{string.Join(" | ", theirs)}] port [{string.Join(" | ", ours)}]");
            }

            // The gate on top of it, which is what a caller actually asks.
            var gate = SourceResidue.Check(c.Source, c.Output) ?? string.Empty;

            if (!string.Equals(gate, c.Gate, StringComparison.Ordinal))
            {
                mismatches.Add($"[{c.Name}] gate script '{c.Gate}' port '{gate}'");
            }
        }

        output.WriteLine($"{oracle.Residue.Count} residue cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches)
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void DefectVerdictsMatchTheScript()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Defects)
        {
            var mine = TranslationDefects.Find(c.Source, c.Output, doNotTranslate: c.DoNotTranslate);

            var theirs = c.Items.Select(i => $"{i.Kind}/{i.Text}/{i.Verdict}").ToList();
            var ours = mine.Select(d => $"{Name(d.Kind)}/{d.Text}/{Name(d.Verdict)}").ToList();

            if (!theirs.SequenceEqual(ours, StringComparer.Ordinal))
            {
                mismatches.Add($"[{c.Name}] script [{string.Join(" | ", theirs)}] port [{string.Join(" | ", ours)}]");
            }
        }

        output.WriteLine($"{oracle.Defects.Count} defect cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches)
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();

        // PowerShell renders these as the enum member name.
        static string Name(object value) => value switch
        {
            DefectKind.Residue => "residue",
            DefectKind.Glue => "glue",
            DefectVerdict.CertainBad => "certain-bad",
            DefectVerdict.CertainOk => "certain-ok",
            DefectVerdict.Ambiguous => "ambiguous",
            _ => value.ToString() ?? "",
        };
    }

    [Fact]
    public void EveryLeakVerdictMatchesTheScriptWordForWord()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Leaks)
        {
            var mine = OutputLeak.Check(c.Source, c.Output, c.Prompt);

            if (!string.Equals(mine, c.Reason, StringComparison.Ordinal))
            {
                mismatches.Add($"[{c.Name}] script {Show(c.Reason)} port {Show(mine)}");
            }
        }

        output.WriteLine($"{oracle.Leaks.Count} leak cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches)
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();

        static string Show(string? r) => r is null ? "<accepted>" : $"'{r}'";
    }

    [Fact]
    public void DocumentStructureIssuesMatchTheScript()
    {
        var oracle = Oracle();

        foreach (var c in oracle.Structure)
        {
            DocumentStructure.Compare(c.Source, c.Output)
                .Should().Equal(c.Issues, $"structure for [{c.Name}]");
        }
    }

    [Fact]
    public void AnIntactDocumentReportsNothingAtAll()
    {
        // The original had to note this: wrapping the result in an array turned
        // an empty list into a one-element one, so a document with nothing wrong
        // reported exactly one problem.
        DocumentStructure.Compare("# H\n```\ncode\n```", "# H\n```\nkod\n```").Should().BeEmpty();
    }

    [Fact]
    public void TheOddFenceThatStartedAllOfThisIsCaught()
    {
        // 44 fences became 37. Every fence after the orphan rendered as one
        // giant code block, and the per-chunk gate could not see it because the
        // fence was closed in a different chunk.
        var issues = DocumentStructure.Compare("```\na\n```", "```\na");

        issues.Should().Contain(i => i.Contains("unbalanced", StringComparison.Ordinal));
        issues.Should().Contain(i => i.Contains("code fences: 2 in source, 1 in output", StringComparison.Ordinal));
    }
}
