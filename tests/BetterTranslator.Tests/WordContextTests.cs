using System;
using System.Text.RegularExpressions;
using BetterTranslator.Engine.Documents;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Looking at one word without losing the line around it.
///
/// A word on its own is not answerable: the model needs the sentence to choose a
/// case and a gender, and the offsets have to survive whatever was hidden from it.
/// </summary>
public sealed class WordContextTests
{
    private static readonly Regex CodeSpan = new("`[^`]*`");

    [Fact]
    public void MaskingKeepsEveryIndexWhereItWas()
    {
        // Every index taken from the masked text is used against the original, so
        // a mask that shortened the line would point every later match at the
        // wrong characters.
        const string Line = "Run `dotnet build` before the release.";

        var masked = WordContext.Mask(Line, CodeSpan);

        masked.Length.Should().Be(Line.Length);
        masked.Should().NotContain("dotnet");
        masked.IndexOf("release", StringComparison.Ordinal)
            .Should().Be(Line.IndexOf("release", StringComparison.Ordinal));
    }

    [Fact]
    public void MaskingAnEmptyLineIsNotAnError()
    {
        WordContext.Mask("", CodeSpan).Should().BeEmpty();
        WordContext.Mask(null, CodeSpan).Should().BeEmpty();
    }

    [Fact]
    public void TheBlankIsWhatMakesTheQuestionAnswerable()
    {
        // Handed the word alone a model translates a dictionary entry; handed the
        // slot it translates what belongs in that slot.
        const string Line = "The store resolves a model by name and falls back to the pinned revision.";
        var index = Line.IndexOf("model", StringComparison.Ordinal);

        var window = WordContext.Window(Line, index, "model".Length, words: 3);

        window.Should().Be("store resolves a ___ by name and");
    }

    [Fact]
    public void AWordAtEitherEndStillGetsAWindow()
    {
        const string Line = "Resolves a model by name.";

        WordContext.Window(Line, 0, "Resolves".Length, 2).Should().Be("___ a model");
        WordContext.Window(Line, Line.Length - 5, 5, 2).Should().Be("model by ___");
    }

    [Fact]
    public void ASpanOutsideTheLineIsRefusedRatherThanTruncated()
    {
        var act = () => WordContext.Window("short line", 5, 99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
