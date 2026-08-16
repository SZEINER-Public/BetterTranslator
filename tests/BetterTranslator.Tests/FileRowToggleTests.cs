using System;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A file row offers no rendered and source switch.
///
/// The switch acts on a body the row does not draw: a file is shown as two
/// chips with an eye on each, and the eye is where its content is opened. Left
/// on the row it read as a control that did nothing.
/// </summary>
public sealed class FileRowToggleTests
{
    [Fact]
    public void AClosedFileOffersNoFormatSwitch()
    {
        var view = new EntryViewModel(Entry(EntryKind.File));

        view.IsMarkdown.Should().BeTrue("the body is still Markdown");
        view.HasFormatToggle.Should().BeTrue("how the body renders is unchanged");
        view.ShowsFormatToggle.Should().BeFalse("a closed file row draws chips, not a body");
    }

    [Fact]
    public void OpeningEitherPreviewBringsTheSwitchBack()
    {
        var view = new EntryViewModel(Entry(EntryKind.File));

        view.ToggleSourcePreviewCommand.Execute(null);
        view.ShowsFormatToggle.Should().BeTrue("the source body is on screen, so the choice means something");

        view.ToggleSourcePreviewCommand.Execute(null);
        view.ShowsFormatToggle.Should().BeFalse();

        view.ToggleResultPreviewCommand.Execute(null);
        view.ShowsFormatToggle.Should().BeTrue("the translated body is on screen");
    }

    [Fact]
    public void OpeningAPreviewAnnouncesTheSwitch()
    {
        var view = new EntryViewModel(Entry(EntryKind.File));
        var announced = 0;

        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EntryViewModel.ShowsFormatToggle))
            {
                announced++;
            }
        };

        view.ToggleSourcePreviewCommand.Execute(null);

        announced.Should().BeGreaterThan(0, "the control appears only if its binding is told to look again");
    }

    [Fact]
    public void AMarkdownMessageStillOffersIt()
    {
        var view = new EntryViewModel(Entry(EntryKind.Sentence));

        view.ShowsFormatToggle.Should().BeTrue();
    }

    /// <summary>
    /// The visibility flag has to travel with the one it derives from, or the
    /// control keeps whatever its binding last read.
    /// </summary>
    [Fact]
    public void TheFlagIsAnnouncedWheneverTheOneItDerivesFromIs()
    {
        var view = new EntryViewModel(Entry(EntryKind.Sentence));
        var announced = 0;

        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EntryViewModel.ShowsFormatToggle))
            {
                announced++;
            }
        };

        view.Result = "Sestaveni selhalo.";

        announced.Should().BeGreaterThan(0);
    }

    private static Entry Entry(EntryKind kind) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = kind,
        FileName = kind == EntryKind.File ? "CLAUDE.md" : null,
        Source = "# Heading\n\nA paragraph with **bold** in it.\n",
        Result = "# Nadpis\n\nOdstavec s **tucnym** textem.\n",
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Done,
        TargetLanguage = "cs",
    };
}
