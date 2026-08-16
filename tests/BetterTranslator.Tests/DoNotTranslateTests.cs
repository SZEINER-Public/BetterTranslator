using System.Linq;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// The do-not-translate lists, and the gate they finally feed.
///
/// `ChunkIntegrity` has accepted strict and declinable term lists since it was
/// ported, and nothing supplied any, so that gate has been inert. These are what
/// turn it on.
/// </summary>
public sealed class DoNotTranslateTests(ITestOutputHelper output)
{
    [Fact]
    public void TheShippedListsAreLoadedAndAreNotEmpty()
    {
        var terms = DoNotTranslate.Load();

        output.WriteLine($"strict ({terms.Strict.Count}): {string.Join(", ", terms.Strict)}");
        output.WriteLine($"declinable ({terms.Declinable.Count}): {string.Join(", ", terms.Declinable)}");

        terms.Strict.Should().NotBeEmpty();
        terms.Declinable.Should().NotBeEmpty();
        terms.Sources.Should().Contain("rag.defaults.json");

        // Generic technical vocabulary only. No brand belongs in the shipped
        // file; a project's own terms are added on top of it.
        terms.Strict.Should().Contain([".NET", "JSON", "GGUF"]);
        terms.Declinable.Should().Contain("PowerShell");
    }

    [Fact]
    public void AProjectAddsToTheDefaultsRatherThanReplacingThem()
    {
        // So a project never has to restate ".NET" in order to add its own name.
        var terms = DoNotTranslate.Load("""{"doNotTranslate":{"strict":["Acme Widget"]}}""");

        terms.Strict.Should().Contain("Acme Widget").And.Contain(".NET");
        terms.Sources.Should().Contain("project");
    }

    [Fact]
    public void AProjectCanOptOutOfTheDefaultsEntirely()
    {
        var terms = DoNotTranslate.Load(
            """{"doNotTranslate":{"replaceDefaults":true,"strict":["OnlyThis"]}}""");

        terms.Strict.Should().ContainSingle().And.Contain("OnlyThis");
        terms.Sources.Should().NotContain("rag.defaults.json");
    }

    [Fact]
    public void ATermOnBothListsIsStrict()
    {
        // Contradictory otherwise -- hidden from the model AND sent to it -- and
        // keeping both would make the behaviour depend on which gate ran first.
        var terms = DoNotTranslate.Load(
            """{"doNotTranslate":{"strict":["Widget"],"declinable":["widget"]}}""");

        terms.Strict.Should().Contain("Widget");
        terms.Declinable.Should().NotContain(d => string.Equals(d, "widget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DuplicatesAreFoldedCaseInsensitively()
    {
        // The gate matches that way too, so keeping both "API" and "api" would
        // only widen the pattern twice.
        var terms = DoNotTranslate.Load("""{"doNotTranslate":{"strict":["api","API","Api"]}}""");

        terms.Strict.Count(s => string.Equals(s, "API", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
    }

    [Fact]
    public void ABrokenProjectFileDoesNotStopTheDefaultsLoading()
    {
        var terms = DoNotTranslate.Load("{ this is not json");

        terms.Strict.Should().NotBeEmpty("the shipped defaults must still load");
    }

    [Fact]
    public void TheGateNowActuallyFiresOnTheShippedTerms()
    {
        var terms = DoNotTranslate.Load();

        // Strict: any change at all is damage.
        ChunkIntegrity.Check("The JSON payload", "Datova zprava", terms.Strict, terms.Declinable)
            .Should().StartWith("do-not-translate term lost: 'JSON'");

        // Declinable: a case ending is correct Czech and must be accepted.
        // "budouci vydani PowerShellu" is right; refusing it left the whole
        // sentence in English.
        ChunkIntegrity.Check("a future PowerShell release", "budouci vydani PowerShellu", terms.Strict, terms.Declinable)
            .Should().BeNull();
    }
}
