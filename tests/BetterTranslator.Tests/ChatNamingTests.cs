using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// D6, unconfirmed: until a model can summarize a chat, its name is the first
/// 40 characters of the first entry. Phase 8 replaces the name for chats still
/// holding a truncated one, so the truncation rule has to be stable.
/// </summary>
public sealed class ChatNamingTests
{
    [Fact]
    public void AShortEntryBecomesTheWholeName() =>
        ChatWorkspaceViewModel.ProvisionalName("workspace").Should().Be("workspace");

    [Fact]
    public void SurroundingWhitespaceIsDropped() =>
        ChatWorkspaceViewModel.ProvisionalName("   Onboarding strings  ").Should().Be("Onboarding strings");

    [Fact]
    public void ExactlyFortyCharactersIsNotTruncated()
    {
        var text = new string('a', 40);

        ChatWorkspaceViewModel.ProvisionalName(text).Should().Be(text).And.HaveLength(40);
    }

    [Fact]
    public void LongerThanFortyIsCutAtForty()
    {
        var text = new string('a', 41);

        ChatWorkspaceViewModel.ProvisionalName(text).Should().HaveLength(40);
    }

    [Fact]
    public void ACutThatLandsOnASpaceDoesNotLeaveATrailingOne()
    {
        // 39 characters, then a space, then more: the cut must not keep the space.
        var text = new string('a', 39) + " tail that runs on";

        var name = ChatWorkspaceViewModel.ProvisionalName(text);

        name.Should().Be(new string('a', 39));
        name.Should().NotEndWith(" ");
    }
}
