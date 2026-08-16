using System;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The placeholder the sidebar row shows while the model names a chat.
///
/// Headless, so no token dictionary is loaded and both waits resolve to nothing:
/// what is pinned here is that the gate degrades to landing the result at once
/// rather than to never landing it. The timed behaviour is the same code the
/// result region has run since the skeleton shipped.
/// </summary>
public sealed class ChatNamingSkeletonTests
{
    [Fact]
    public void WithNoTokensTheResultLandsAtOnceAndNoPlaceholderAppears()
    {
        var shown = new List<bool>();
        var gate = new SkeletonGate(shown.Add);
        var applied = false;

        gate.Begin();
        gate.Settle(() => applied = true);

        applied.Should().BeTrue();
        gate.IsShowing.Should().BeFalse();
        shown.Should().NotContain(true, "nothing was owed a delay, so nothing was shown");
    }

    [Fact]
    public void SettlingWithoutBeginningStillApplies()
    {
        var applied = false;

        new SkeletonGate(_ => { }).Settle(() => applied = true);

        applied.Should().BeTrue();
    }

    [Fact]
    public void TheGateRefusesAMissingApply()
    {
        var act = () => new SkeletonGate(_ => { }).Settle(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BeginningTwiceDoesNotStrandThePlaceholder()
    {
        var gate = new SkeletonGate(_ => { });
        var applied = 0;

        gate.Begin();
        gate.Begin();
        gate.Settle(() => applied++);

        applied.Should().Be(1);
        gate.IsShowing.Should().BeFalse();
    }

    [Fact]
    public void ThePlaceholderCanGoUpWithNoDelayOwed()
    {
        // The chat's own case: the name is asked for only once the translation
        // is back, so there are seconds to cover and nothing to protect against
        // a flash. The truncated source must not be shown during them.
        var row = Row("Explain how prompt caching lowers the co");

        row.Naming.BeginNow();

        row.IsNaming.Should().BeTrue();
        row.Naming.IsShowing.Should().BeTrue();

        row.Naming.Settle(() => row.Name = "Úspora nákladů díky ukládání");

        row.IsNaming.Should().BeFalse();
        row.Name.Should().Be("Úspora nákladů díky ukládání");
    }

    [Fact]
    public void AFailedNamingRevealsTheTruncationItWasHiding()
    {
        const string Truncated = "Explain how prompt caching lowers the co";
        var row = Row(Truncated);

        row.Naming.BeginNow();
        row.Naming.Settle(() => { });

        row.IsNaming.Should().BeFalse();
        row.Name.Should().Be(Truncated, "the truncation is the fallback, not a defect to hide forever");
    }

    [Fact]
    public void TheRowExposesThePlaceholderAndItsWidth()
    {
        var row = Row("Default output naming: <name>.<to>.<e");

        row.IsNaming.Should().BeFalse();
        row.NameLength.Should().Be(37);

        row.Naming.Begin();
        row.Naming.Settle(() => row.Name = "Výchozí pojmenování výstupu");

        row.Name.Should().Be("Výchozí pojmenování výstupu");
        row.NameLength.Should().Be(27, "the bar is sized from the name, so the width has to follow it");
        row.IsNaming.Should().BeFalse();
    }

    [Fact]
    public void TheRowMirrorsANewNameOntoItsChat()
    {
        var row = Row("How do I publish a single self-contained");

        row.Naming.Settle(() => row.Name = "Jak vydat spustitelný soubor");

        row.Chat.Name.Should().Be("Jak vydat spustitelný soubor");
    }

    private static ChatRowViewModel Row(string name) => new(
        new Chat
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        },
        new ClockService());
}
