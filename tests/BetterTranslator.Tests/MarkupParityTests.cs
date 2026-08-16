using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>What `tests\parity\markup\oracle.ps1` recorded the reference doing.</summary>
public sealed record MarkupOracle
{
    [JsonPropertyName("protect")] public IReadOnlyList<ProtectCase> Protect { get; init; } = [];

    [JsonPropertyName("sentinels")] public IReadOnlyList<SentinelCase> Sentinels { get; init; } = [];

    [JsonPropertyName("placeholders")] public IReadOnlyList<PlaceholderCase> Placeholders { get; init; } = [];

    [JsonPropertyName("integrity")] public IReadOnlyList<IntegrityCase> Integrity { get; init; } = [];

    [JsonPropertyName("notes")] public IReadOnlyList<NoteCase> Notes { get; init; } = [];

    public sealed record ProtectCase
    {
        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("text")] public string? Text { get; init; }

        [JsonPropertyName("store")] public IReadOnlyList<string> Store { get; init; } = [];

        [JsonPropertyName("restored")] public string? Restored { get; init; }

        /// <summary>The reference refuses this input; the port is not held to it.</summary>
        [JsonPropertyName("unsupported")] public bool Unsupported { get; init; }
    }

    public sealed record SentinelCase
    {
        [JsonPropertyName("text")] public string Text { get; init; } = "";

        [JsonPropertyName("ids")] public IReadOnlyList<int> Ids { get; init; } = [];
    }

    public sealed record PlaceholderCase
    {
        [JsonPropertyName("text")] public string Text { get; init; } = "";

        [JsonPropertyName("found")] public IReadOnlyList<string> Found { get; init; } = [];
    }

    public sealed record IntegrityCase
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("translated")] public string Translated { get; init; } = "";

        [JsonPropertyName("doNotTranslate")] public IReadOnlyList<string> DoNotTranslate { get; init; } = [];

        [JsonPropertyName("declinable")] public IReadOnlyList<string> Declinable { get; init; } = [];

        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    public sealed record NoteCase
    {
        [JsonPropertyName("source")] public string Source { get; init; } = "";

        [JsonPropertyName("translated")] public string Translated { get; init; } = "";

        [JsonPropertyName("cleaned")] public string Cleaned { get; init; } = "";
    }
}

/// <summary>
/// The C# port of `markup-guard.ps1` against the script itself.
///
/// The reason strings are compared verbatim, not just their presence: a caller
/// logs them and a human reads them to work out what the model did. A gate that
/// fires for the right chunk with the wrong sentence is still a divergence.
/// </summary>
public sealed class MarkupParityTests(ITestOutputHelper output)
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "parity", "markup", "oracle.json");

    private static MarkupOracle Oracle()
    {
        File.Exists(FixturePath).Should().BeTrue($"the parity fixture must be copied to the output: {FixturePath}");

        return BomSafeJson.Deserialize<MarkupOracle>(File.ReadAllBytes(FixturePath))!;
    }

    [Fact]
    public void ProtectAndRestoreMatchTheScript()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();
        var checkedCases = 0;

        foreach (var c in oracle.Protect.Where(c => !c.Unsupported))
        {
            checkedCases++;
            var mine = MarkupGuard.Protect(c.Source);

            if (!string.Equals(mine.Text, c.Text, StringComparison.Ordinal))
            {
                mismatches.Add($"text: script '{c.Text}' port '{mine.Text}'");
                continue;
            }

            if (!mine.Store.SequenceEqual(c.Store, StringComparer.Ordinal))
            {
                mismatches.Add($"store: script [{string.Join("|", c.Store)}] port [{string.Join("|", mine.Store)}]");
                continue;
            }

            var restored = MarkupGuard.Restore(mine.Text, mine.Store);

            if (!string.Equals(restored, c.Restored, StringComparison.Ordinal))
            {
                mismatches.Add($"restore: script '{c.Restored}' port '{restored}'");
            }
        }

        output.WriteLine($"{checkedCases} protect cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches.Take(10))
        {
            output.WriteLine("  " + m);
        }

        checkedCases.Should().BeGreaterThan(0);
        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void ProtectingAndRestoringIsLosslessForEveryFixture()
    {
        // The property the whole mechanism rests on: the originals come back byte
        // for byte. A round trip that is merely close is the 48 KB README again.
        foreach (var c in Oracle().Protect.Where(c => !c.Unsupported))
        {
            var p = MarkupGuard.Protect(c.Source);

            MarkupGuard.Restore(p.Text, p.Store).Should().Be(c.Source);
        }
    }

    [Fact]
    public void RestoreTakesTheHighestIndexFirst()
    {
        // [[1]] is a prefix of [[10]]. Ascending replacement turns [[10]] into
        // the first store entry followed by a stray '0]]'.
        var store = Enumerable.Range(0, 12).Select(i => $"<{i}>").ToList();

        MarkupGuard.Restore("[[10]] and [[1]]", store).Should().Be("<10> and <1>");
    }

    [Fact]
    public void SentinelIdsMatchTheScript()
    {
        var oracle = Oracle();

        foreach (var c in oracle.Sentinels)
        {
            MarkupGuard.SentinelIds(c.Text).Should().Equal(c.Ids, $"ids for '{c.Text}'");
        }
    }

    [Fact]
    public void PlaceholdersMatchTheScript()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Placeholders)
        {
            var mine = MarkupGuard.Placeholders(c.Text);

            // Compared as a bag, not a sequence. The reference sorts through NLS
            // collation, which .NET 10 cannot reproduce without forcing the whole
            // app off ICU; the port sorts ordinally instead. Which placeholders
            // were found is the contract -- the order is an implementation
            // detail of comparing two lists pairwise, and the verdicts that
            // depend on it are asserted verbatim in
            // EveryIntegrityVerdictMatchesTheScriptWordForWord.
            if (!mine.Order(StringComparer.Ordinal).SequenceEqual(c.Found.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                mismatches.Add($"'{c.Text}' -> script [{string.Join("|", c.Found)}] port [{string.Join("|", mine)}]");
            }
        }

        output.WriteLine($"{oracle.Placeholders.Count} placeholder cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches)
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void ThePortsOwnOrderingIsOrdinalAndLocaleFree()
    {
        // The divergence, pinned so it stays deliberate. A structural gate whose
        // verdict could shift with the machine's culture would be far worse than
        // a list in a different order from the reference's.
        MarkupGuard.Placeholders("Mixed {{a}} {0} :user %s {b.c}")
            .Should().Equal("%s", ":user", "{0}", "{b.c}", "{{a}}");
    }

    [Fact]
    public void TheVerdictDoesNotDependOnWhichOrderTheListsAreSortedIn()
    {
        // Why the divergence above is safe: both sides are sorted the same way
        // and compared pairwise, so the sort is only a way to test two bags for
        // equality. Same placeholders in a different textual order is still a
        // pass; a genuinely different set is still a failure.
        ChunkIntegrity.Check("Mixed {{a}} {0} :user %s {b.c}", "Smes %s :user {b.c} {0} {{a}}")
            .Should().BeNull("the same placeholders reordered by the translation are not a defect");

        ChunkIntegrity.Check("Mixed {{a}} {0} :user %s {b.c}", "Smes %s :user {b.c} {0} {{z}}")
            .Should().StartWith("placeholder altered", "a genuinely different placeholder is");
    }

    [Fact]
    public void DotNetApiProseDoesNotManufacturePlaceholders()
    {
        // The defect the colon lookbehind exists for: '::WriteAllText' and
        // '-Confirm:$false' were read as Laravel placeholders, so prose about
        // .NET APIs was rejected for a defect that was never there.
        MarkupGuard.Placeholders("[System.IO.File]::WriteAllText").Should().BeEmpty();
        MarkupGuard.Placeholders("-Confirm:$false").Should().BeEmpty();

        // And the form it must still catch.
        MarkupGuard.Placeholders("Welcome :user").Should().Equal(":user");
    }

    [Fact]
    public void EveryIntegrityVerdictMatchesTheScriptWordForWord()
    {
        var oracle = Oracle();
        var mismatches = new List<string>();

        foreach (var c in oracle.Integrity)
        {
            var mine = ChunkIntegrity.Check(c.Source, c.Translated, c.DoNotTranslate, c.Declinable);

            if (!string.Equals(mine, c.Reason, StringComparison.Ordinal))
            {
                mismatches.Add($"[{c.Name}] script: {Show(c.Reason)}  port: {Show(mine)}");
            }
        }

        output.WriteLine($"{oracle.Integrity.Count} integrity cases, {mismatches.Count} mismatched");
        foreach (var m in mismatches)
        {
            output.WriteLine("  " + m);
        }

        mismatches.Should().BeEmpty();

        static string Show(string? r) => r is null ? "<accepted>" : $"'{r}'";
    }

    [Fact]
    public void TheDefectsTheGuardWasWrittenForAreStillCaught()
    {
        // Named individually so a regression says which real incident came back,
        // rather than only that some fixture moved.
        ChunkIntegrity.Check("See [[0]] here", "Viz zde")
            .Should().StartWith("protected block count changed", "a dropped fence corrupts every section after it");

        ChunkIntegrity.Check("## Choosing a model", "Vyber modelu")
            .Should().StartWith("line prefix changed", "24 of 43 headings were lost this way");

        ChunkIntegrity.Check("| a | b |", "| a b |")
            .Should().StartWith("table cell count changed", "19 of 124 table rows were lost this way");

        ChunkIntegrity.Check("A choice of things", "Vyber veci https://www.cnet.com/news/")
            .Should().StartWith("url count changed", "a translation cannot introduce a link");

        ChunkIntegrity.Check("Use --no-serve here", "Pouzijte --ne-serve zde")
            .Should().StartWith("command option", "a translated flag is simply wrong");
    }

    [Fact]
    public void ACorrectlyInflectedTermIsAcceptedWhenItIsDeclinable()
    {
        // "budouci vydani LLMsteru" is correct Czech, the locative case, and was
        // being refused - so the sentence stayed English for no good reason.
        ChunkIntegrity.Check("a future llmster release", "budouci vydani LLMsteru", ["llmster"], ["llmster"])
            .Should().BeNull();

        // Off the declinable list, any change at all is damage.
        ChunkIntegrity.Check("The JSON payload", "Datova zprava", ["JSON"])
            .Should().StartWith("do-not-translate term lost");
    }

    [Fact]
    public void AnUnbalancedSourceLineIsNotJudgedOnItsBrackets()
    {
        // Prose wraps, so a parenthetical opens on one line and closes on the
        // next. Counting those per line reported six correct translations as
        // damaged.
        ChunkIntegrity.Check("Through the proxy (", "Pres proxy )").Should().BeNull();
    }

    [Fact]
    public void TranslatorNotesAreStrippedExactlyAsTheScriptStripsThem()
    {
        var oracle = Oracle();

        foreach (var c in oracle.Notes)
        {
            ChunkIntegrity.RemoveTranslatorNote(c.Source, c.Translated)
                .Should().Be(c.Cleaned, $"cleaning '{c.Translated}'");
        }
    }

    /// <summary>Regenerates the oracle and fails if the reference has moved.</summary>
    [ParityFact]
    public void TheFixtureStillMatchesTheReferenceScript()
    {
        ParityOracle.Regenerate("markup", FixturePath, output);
    }
}
