using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Slop;
using BetterTranslator.Engine.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>One call the reference's own fixture suite made, and what it answered.</summary>
public sealed record RecordedCall
{
    [JsonPropertyName("fn")] public string Function { get; init; } = "";

    [JsonPropertyName("args")] public IReadOnlyList<RecordedArg> Args { get; init; } = [];

    [JsonPropertyName("result")] public JsonElement Result { get; init; }
}

public sealed record RecordedArg
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("value")] public JsonElement Value { get; init; }
}

public sealed record GuardFixtureOracle
{
    [JsonPropertyName("calls")] public IReadOnlyList<RecordedCall> Calls { get; init; } = [];
}

/// <summary>
/// The port replayed against the reference's own fixture corpus.
///
/// `test-guard.ps1` is 844 lines of inline cases, every one "drawn from damage
/// that actually happened or from the exact failure the gate was written to
/// stop". Its inputs are the most valuable thing in the reference repository and
/// they are not written down anywhere a port can read, so the oracle records
/// them by wrapping each guard function while the fixture suite runs.
///
/// What that buys over the hand-built cases: these were not chosen by whoever
/// wrote the port, so they cannot have been chosen to suit it.
/// </summary>
public sealed class GuardFixtureParityTests(ITestOutputHelper output)
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "parity", "guard-fixtures", "oracle.json");

    private static GuardFixtureOracle Oracle()
    {
        File.Exists(FixturePath).Should().BeTrue($"the recorded corpus must be copied to the output: {FixturePath}");

        return BomSafeJson.Deserialize<GuardFixtureOracle>(File.ReadAllBytes(FixturePath))!;
    }

    private static string? Text(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => e.ToString(),
        };

    private static IReadOnlyList<string> List(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.Array => [.. e.EnumerateArray().Select(x => Text(x) ?? string.Empty)],
            JsonValueKind.String => [e.GetString()!],
            _ => [],
        };

    /// <summary>
    /// A named argument, or the positional one at that index. PowerShell records
    /// "-Source value" as a pair and a bare value as "[0]", and the fixtures use
    /// both.
    /// </summary>
    private static JsonElement? Arg(RecordedCall call, string name, int position)
    {
        foreach (var a in call.Args)
        {
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return a.Value;
            }
        }

        var positional = call.Args.FirstOrDefault(a => a.Name == $"[{position}]");
        return positional?.Value;
    }

    private static string Str(RecordedCall call, string name, int position) =>
        Arg(call, name, position) is { } e ? Text(e) ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> Strs(RecordedCall call, string name) =>
        call.Args.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a
            ? List(a.Value)
            : [];

    private static int Int(RecordedCall call, string name, int fallback) =>
        call.Args.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a
            && int.TryParse(Text(a.Value), out var v)
                ? v
                : fallback;

    [Fact]
    public void TheRecordedCorpusIsSubstantial()
    {
        var oracle = Oracle();

        oracle.Calls.Should().HaveCountGreaterThan(100, "the fixture suite runs 175 checks");

        foreach (var group in oracle.Calls.GroupBy(c => c.Function).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{group.Key,-24} {group.Count()}");
        }
    }

    [Fact]
    public void EveryRecordedCallIsAnsweredTheSameWay()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();
        var replayed = 0;

        foreach (var call in oracle.Calls)
        {
            string? theirs = Text(call.Result);
            string? ours;

            switch (call.Function)
            {
                case "Get-SentinelIds":
                    ours = string.Join(",", MarkupGuard.SentinelIds(Str(call, "Text", 0)));
                    theirs = string.Join(",", List(call.Result));
                    break;

                case "Get-PlaceholdersSafe":
                    // Compared as a bag: the reference sorts through NLS
                    // collation, which .NET 10 cannot reproduce. See B28.
                    ours = string.Join(",", MarkupGuard.Placeholders(Str(call, "Text", 0)).Order(StringComparer.Ordinal));
                    theirs = string.Join(",", List(call.Result).Order(StringComparer.Ordinal));
                    break;

                case "Test-ChunkIntegrity":
                    ours = ChunkIntegrity.Check(
                        Str(call, "Source", 0),
                        Str(call, "Translated", 1),
                        Strs(call, "DoNotTranslate"),
                        Strs(call, "Declinable"));
                    break;

                case "Test-OutputLeak":
                    ours = OutputLeak.Check(
                        Str(call, "Source", 0),
                        Str(call, "Translated", 1),
                        Str(call, "Prompt", 2));
                    break;

                case "Test-SourceResidue":
                    ours = SourceResidue.Check(
                        Str(call, "Source", 0),
                        Str(call, "Translated", 1),
                        Int(call, "MinRun", 4)) ?? string.Empty;
                    theirs ??= string.Empty;
                    break;

                case "Test-ListingLine":
                    ours = SourceResidue.IsListingLine(Str(call, "Line", 0)).ToString();
                    break;

                case "Test-DocumentStructure":
                    ours = string.Join(" ;; ", DocumentStructure.Compare(Str(call, "Source", 0), Str(call, "Translated", 1)));
                    theirs = string.Join(" ;; ", List(call.Result));
                    break;

                case "Remove-TranslatorNote":
                    ours = ChunkIntegrity.RemoveTranslatorNote(Str(call, "Source", 0), Str(call, "Translated", 1));
                    break;

                case "Get-SourceResidue":
                    ours = string.Join(" ;; ", SourceResidue
                        .Find(Str(call, "Source", 0), Str(call, "Translated", 1), Int(call, "MinRun", 3), Str(call, "Protect", 3))
                        .Select(r => $"{r.Text}|{r.Words}|"));
                    theirs = string.Join(" ;; ", List(call.Result));
                    break;

                case "Get-TranslationDefect":
                    ours = string.Join(" ;; ", TranslationDefects
                        .Find(
                            Str(call, "Source", 0),
                            Str(call, "Translated", 1),
                            Str(call, "Protect", 2),
                            Strs(call, "DoNotTranslate"))
                        .Select(d => $"{d.Text}|{d.Words}|{Verdict(d.Verdict)}"));
                    theirs = string.Join(" ;; ", List(call.Result));
                    break;

                case "Invoke-SlopValidator":
                {
                    // The recorded Slop argument is the shipped default for cs --
                    // "@{language=cs; ...}" -- and the recorded placeholder
                    // patterns are byte-identical to the ported ones, so the
                    // default is the right config to replay with.
                    var r = SlopValidator.Validate(
                        Str(call, "Source", 0),
                        Str(call, "Translated", 1),
                        SlopConfig.Default("cs"));

                    ours = $"{r.Text}||{SlopName(r.Verdict)}";
                    break;
                }

                case "Repair-Symbols":
                {
                    var (text, _) = SlopValidator.RepairSymbols(
                        Str(call, "Source", 0), Str(call, "Translated", 1), SlopConfig.Default("cs").Symbols);

                    ours = $"{text}||";
                    break;
                }

                case "Repair-Content":
                {
                    var (text, _) = SlopValidator.RepairContent(
                        Str(call, "Translated", 0), SlopConfig.Default("cs").Content);

                    ours = $"{text}||";
                    break;
                }

                case "Get-RagPlaceholders":
                    ours = string.Join(",", SlopValidator
                        .Placeholders(Str(call, "Text", 0), Strs(call, "Patterns"))
                        .Order(StringComparer.Ordinal));
                    theirs = string.Join(",", List(call.Result).Order(StringComparer.Ordinal));
                    break;

                case "Get-DocumentTerms":
                    ours = string.Join(" ;; ", DocumentTerms
                        .Find(Str(call, "Text", 0), Int(call, "MinFrequency", 2))
                        .Select(t => $"{t.Term}|{t.Count}"));
                    theirs = string.Join(" ;; ", List(call.Result));
                    break;

                case "Get-TermInconsistency":
                {
                    // The recorded Terms argument is a list of "term|count"
                    // rows, which is what the recorder renders them as.
                    var terms = Strs(call, "Terms")
                        .Select(s => s.Split('|'))
                        .Where(p => p.Length >= 2 && int.TryParse(p[1], out _))
                        .Select(p => new DocumentTerm(p[0], int.Parse(p[1])))
                        .ToList();

                    ours = string.Join(" ;; ", DocumentTerms
                        .Inconsistencies(Strs(call, "SourceLines"), Strs(call, "TargetLines"), terms,
                            Int(call, "MinOccurrences", 3))
                        .Select(t => $"{t.Term}|{t.Kept}|{t.Translated}|{t.Majority}"));

                    theirs = string.Join(" ;; ", List(call.Result));
                    break;
                }

                case "Get-GlossaryHint":
                    // The recorded Glossary argument names cs.example.md, which
                    // is the file embedded here, so the shipped tables are the
                    // right ones to replay against.
                    ours = Glossary.Hint(Glossary.Shipped("cs"), Str(call, "Text", 1));
                    break;

                case "Test-Glossary":
                    ours = Glossary.Check(
                        Glossary.Shipped("cs"),
                        Str(call, "Source", 1),
                        Str(call, "Translated", 2)) ?? string.Empty;
                    theirs ??= string.Empty;
                    break;

                default:
                    continue;   // recorded but not replayed here
            }

            replayed++;

            if (!string.Equals(ours ?? string.Empty, theirs ?? string.Empty, StringComparison.Ordinal))
            {
                mismatches.Add($"{call.Function}: script [{theirs}] port [{ours}]");
            }
        }

        output.WriteLine($"{replayed} of {oracle.Calls.Count} recorded calls replayed, {mismatches.Count} mismatched");
        foreach (var m in mismatches.Take(25))
        {
            output.WriteLine("  " + m);
        }

        replayed.Should().BeGreaterThan(100, "most of the corpus must actually be exercised");
        mismatches.Should().BeEmpty();

        static string Verdict(DefectVerdict v) => v switch
        {
            DefectVerdict.CertainBad => "certain-bad",
            DefectVerdict.CertainOk => "certain-ok",
            _ => "ambiguous",
        };

        static string SlopName(SlopVerdict v) => v switch
        {
            SlopVerdict.Ok => "ok",
            SlopVerdict.Content => "content",
            _ => "integrity",
        };
    }

    /// <summary>Regenerates the corpus and fails if the fixture suite has moved.</summary>
    [ParityFact]
    public void TheCorpusStillMatchesTheReferenceFixtures()
    {
        ParityOracle.Regenerate("guard-fixtures", FixturePath, output);
    }
}
