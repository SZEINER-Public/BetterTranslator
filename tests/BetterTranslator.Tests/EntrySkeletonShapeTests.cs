using System;
using System.Linq;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The shape the result placeholder takes: one bar per line of the send, each as
/// wide as its own line.
///
/// A character count could only ever guess how many lines were coming, and it
/// capped at eight, so a two-hundred-line message showed eight bars and then
/// jumped to its full height when the answer landed.
/// </summary>
public sealed class EntrySkeletonShapeTests
{
    [Fact]
    public void OneLengthPerLineOfTheSend()
    {
        var view = new EntryViewModel(Entry("first line\nsecond\nthird one here"));

        view.SourceLineLengths.Should().Equal(10, 6, 14);
    }

    [Fact]
    public void BlankLinesAreKeptBecauseTheyArePartOfTheShape()
    {
        var view = new EntryViewModel(Entry("one\n\nthree"));

        view.SourceLineLengths.Should().Equal(3, 0, 5);
    }

    [Fact]
    public void WindowsLineEndingsDoNotAddPhantomCharacters()
    {
        var view = new EntryViewModel(Entry("one\r\ntwo"));

        // A stray carriage return would widen every bar by one character.
        view.SourceLineLengths.Should().Equal(3, 3);
    }

    [Fact]
    public void ASingleLineIsASingleBar()
    {
        var view = new EntryViewModel(Entry("The build is green."));

        view.SourceLineLengths.Should().ContainSingle().Which.Should().Be(19);
    }

    [Fact]
    public void TheShapeMatchesTheSendItCameFrom()
    {
        var view = new EntryViewModel(Entry(SpecCorpus.CommandSurface));

        view.SourceLineLengths.Should().HaveCount(
            SpecCorpus.CommandSurface.ReplaceLineEndings("\n").Split('\n').Length);

        view.SourceLineLengths.Sum().Should().BeLessThanOrEqualTo(view.SourceLength);
    }

    [Fact]
    public void TheShapeIsComputedOnceAndReused()
    {
        var view = new EntryViewModel(Entry("one\ntwo"));

        view.SourceLineLengths.Should().BeSameAs(view.SourceLineLengths);
    }

    private static Entry Entry(string source) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.Sentence,
        Source = source,
        Result = string.Empty,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Pending,
        TargetLanguage = "Czech",
    };
}
