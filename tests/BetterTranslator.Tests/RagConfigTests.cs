using System.Linq;
using BetterTranslator.Engine.Models;
using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// The per-language configuration, and the two things it puts into the prompt:
/// the target-language rules and the glossary for the text being translated.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class RagConfigTests(ITestOutputHelper output)
{
    /// <summary>
    /// The example, which is what the glossary MECHANICS are tested against. What
    /// is actually in force is a different question, and it has its own test
    /// below.
    /// </summary>
    private static readonly GlossaryTables Example = Glossary.Shipped("cs");

    [Fact]
    public void CzechShipsRulesAndASecondPass()
    {
        var rag = RagConfig.For("cs");

        output.WriteLine($"rules {rag.RulesText.Length} chars, glossary {rag.Glossary.Terms.Count} terms");

        rag.SecondPass.Should().BeTrue("measured for Czech in the shipped defaults");
        rag.RulesText.Should().NotBeEmpty().And.Contain("vykání", "the register rule is the first thing in the file");
        rag.Slop.Symbols.QuoteStyle.Should().Be("cs");
    }

    [Fact]
    public void NoGlossaryIsInForceUntilTheReaderWritesOne()
    {
        // A glossary is a claim about ONE body of documents, not about the
        // language. The shipped file is the reference project's own vocabulary --
        // it requires "store" to become "úložiště", which is right for a model
        // store and wrong for the shop Mr. White went to last night. Loaded as a
        // default it put that instruction into every prompt and then refused
        // every correct answer that ignored it.
        RagConfig.For("cs").Glossary.IsEmpty
            .Should().BeTrue("the shipped glossary is a worked example, not a default in force");

        Example.Terms.Should().NotBeEmpty("the example is still there to start from");
    }

    [Fact]
    public void ALanguageWithNoRulesFileGetsAnEmptySuffixRatherThanNothingWorking()
    {
        // Phase 1 never degrades the baseline: with no rules the prompt is
        // exactly what it was before any of this existed.
        var rag = RagConfig.For("de");

        rag.RulesText.Should().BeEmpty();
        rag.SecondPass.Should().BeFalse();
        rag.Slop.Symbols.QuoteStyle.Should().Be("none");
        SlopValidator.SystemSuffix(rag.RulesText).Should().BeEmpty();
    }

    [Fact]
    public void TheRulesAreCappedSoTheyCannotEatTheChunkBudget()
    {
        // They are a prompt prefix, not a document.
        RagConfig.For("cs").RulesText.Length.Should().BeLessThanOrEqualTo(RagConfig.MaxRulesLength);
    }

    [Fact]
    public void TheRulesReachTheSystemPromptForAModelWithNoTrainedShape()
    {
        var rag = RagConfig.For("cs");

        var built = TranslationPromptBuilder.Build(
            "EuroLLM-9B-Instruct-Q4_K_M.gguf", "The build failed.", "English", "en", "Czech", "cs",
            rulesText: rag.RulesText,
            templateOverride: ChatTemplate.ChatMl);

        built.Should().NotBeNull();
        built!.Text.Should().Contain("Target-language rules, follow them exactly:")
            .And.Contain("vykání");
    }

    [Fact]
    public void TheGlossaryHintCarriesOnlyTheTermsActuallyPresent()
    {
        var hit = Glossary.Hint(Example, "run.bat provisions the store", "Czech");
        var miss = Glossary.Hint(Example, "the quick brown fox", "Czech");

        output.WriteLine(hit);

        hit.Should().Contain("provisions").And.Contain("store");
        miss.Should().BeEmpty("a dictionary the model has to read past is worse than none");
    }

    [Fact]
    public void TheGlossaryGateFiresOnAMissingTermAndAnInventedWord()
    {
        Glossary.Check(Example,"`run.bat` provisions and starts a local endpoint",
                "`run.bat` provisions a spustí lokální koncový bod")
            .Should().StartWith("glossary term missing");

        Glossary.Check(Example,"# run.bat - repo-local LM Studio bootstrap",
                "# Spusťte soubor run.bat - místní zavaděč")
            .Should().StartWith("word added with no basis in the source");

        // And accepts the correct answers, which is the half that matters.
        Glossary.Check(Example,"`run.bat` provisions and starts a local endpoint",
                "`run.bat` zprovozní a spustí lokální koncový bod")
            .Should().BeNull();

        Glossary.Check(Example,"the log file is written by run.bat",
                "soubor protokolu zapisuje run.bat")
            .Should().BeNull("'soubor' is justified by 'file' in the source");
    }

    [Fact]
    public void ATermHiddenInsideCodeIsNotDemandedOfTheTranslation()
    {
        // "store" occurs in `--model-store`, a protected span the model never
        // sees and never translates, so demanding the Czech term for it rejected
        // correct lines.
        Glossary.Check(Example,"pass `--model-store` to override", "předejte `--model-store` pro přepsání")
            .Should().BeNull();
    }
}
