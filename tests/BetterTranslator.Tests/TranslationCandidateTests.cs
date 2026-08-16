using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What is worth sending to the model.
///
/// Excluding a line here rather than translating it and rejecting the answer is
/// the whole saving: a line of pure markup sent to a model comes back changed,
/// fails a structural gate, and costs a request and a retry to end up exactly
/// where it started.
/// </summary>
public sealed class TranslationCandidateTests
{
    private static readonly DoNotTranslateLists Terms = DoNotTranslate.Load();

    [Fact]
    public void OrdinaryProseIsWorthSending()
    {
        TranslationCandidate.IsWorthSending("The build failed because the store was missing.")
            .Should().BeTrue();
    }

    [Fact]
    public void PureMarkupIsNot()
    {
        TranslationCandidate.IsWorthSending("|---|---|---|").Should().BeFalse();
        TranslationCandidate.IsWorthSending("-----").Should().BeFalse();
        TranslationCandidate.IsWorthSending("").Should().BeFalse();
        TranslationCandidate.IsWorthSending("   ").Should().BeFalse();
        TranslationCandidate.IsWorthSending(null).Should().BeFalse();
    }

    [Fact]
    public void ALineThatIsOnlyCodeOrLinksIsNot()
    {
        TranslationCandidate.IsWorthSending("`dotnet build BetterTranslator.sln`").Should().BeFalse();
        TranslationCandidate.IsWorthSending("https://example.com/some/long/path").Should().BeFalse();
        TranslationCandidate.IsWorthSending(@"C:\Users\someone\models\file.gguf").Should().BeFalse();
        TranslationCandidate.IsWorthSending("--no-serve --verbose --dry-run").Should().BeFalse();
        TranslationCandidate.IsWorthSending("[[0]] [[1]] [[2]]").Should().BeFalse();
    }

    [Fact]
    public void ALineWhoseOnlyWordsAreProtectedTermsIsNot()
    {
        // Once the do-not-translate terms are removed there is nothing left to
        // translate, so sending it can only produce damage.
        TranslationCandidate.IsWorthSending("JSON, XML, YAML, CSV", Terms.Strict).Should().BeFalse();

        // The same line without the term list is a different question, and the
        // answer is allowed to differ.
        TranslationCandidate.IsWorthSending("The payload format is negotiated per request.", Terms.Strict)
            .Should().BeTrue();
    }

    [Fact]
    public void NumbersAndPunctuationAreNotWords()
    {
        TranslationCandidate.IsWorthSending("1. 2. 3. 4. 5.").Should().BeFalse();
        TranslationCandidate.IsWorthSending("| 1 | 2 | 3 |").Should().BeFalse();
    }

    [Fact]
    public void TheLinePromptCarriesNoRulesFile()
    {
        // Measured: with forty lines of rules attached to a single short line the
        // model answered about the rules instead of translating -- it pasted them
        // into the document and invented explanations.
        var prompt = TranslationCandidate.LinePrompt("Czech");

        prompt.Should().Contain("Output exactly one line")
            .And.Contain("MEANING of the whole sentence")
            .And.Contain("Czech");

        prompt.Length.Should().BeLessThan(500, "brevity is what keeps the answer a translation");
    }
}
