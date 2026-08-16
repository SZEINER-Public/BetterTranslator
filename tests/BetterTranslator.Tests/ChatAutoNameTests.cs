using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Languages;
using BetterTranslator.Engine.Chats;
using BetterTranslator.Engine.Models;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// D6: the model that translated the first message names the chat.
///
/// The installed models are translation fine-tunes, so most of this is about
/// what happens when the model does not answer the question it was asked.
/// </summary>
public sealed class ChatAutoNameTests
{
    private const string Message =
        "Default output naming: <name>.<to>.<ext> beside the input unless --out is given.";

    [Theory]
    [InlineData("Výchozí pojmenování výstupu", "Výchozí pojmenování výstupu")]
    [InlineData("\"Výchozí pojmenování\"", "Výchozí pojmenování")]
    [InlineData("**Výchozí pojmenování**", "Výchozí pojmenování")]
    [InlineData("Název: Výchozí pojmenování", "Výchozí pojmenování")]
    [InlineData("Title: Výchozí pojmenování", "Výchozí pojmenování")]
    [InlineData("Výchozí pojmenování.", "Výchozí pojmenování")]
    [InlineData("Výchozí\n  pojmenování", "Výchozí pojmenování")]
    [InlineData("[[1]] Výchozí pojmenování", "Výchozí pojmenování")]
    public void TheAnswerIsCleanedIntoATitle(string answer, string expected) =>
        ChatTitle.Clean(answer, Message).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("x")]
    public void NothingUsableIsRejected(string? answer) =>
        ChatTitle.Clean(answer, Message).Should().BeNull();

    [Fact]
    public void TheMessageItselfIsNotATitle()
    {
        // The translation model's favourite failure: answering with the text it
        // was given. Stored as a name it would settle the chat on a truncation.
        ChatTitle.Clean(Message, Message).Should().BeNull();
        ChatTitle.Clean("Default output naming: <name>", Message).Should().BeNull();
    }

    [Fact]
    public void ATitleIsCappedAtFiveWords() =>
        ChatTitle.Clean("jedna dva tri ctyri pet sest sedm", Message)
            .Should().Be("jedna dva tri ctyri pet");

    [Fact]
    public void TheLengthCapCutsBetweenWords()
    {
        var title = ChatTitle.Clean("pojmenovani vystupnich souboru prekladace nastroje", Message);

        title.Should().NotBeNull();
        title!.Length.Should().BeLessThanOrEqualTo(ChatTitle.MaxLength);
        title.Should().NotEndWith(" ");
        "pojmenovani vystupnich souboru prekladace nastroje".Should().StartWith(title);
    }

    [Fact]
    public void NoAcceptedTitleIsLongEnoughForTheSidebarToTrimIt()
    {
        // Measured on screen: the row ellipsized "Publikování spustitelného
        // souboru" at twenty-nine characters, and a name with dots after it is a
        // name the reader cannot finish.
        string[] answers =
        [
            "Publikování spustitelného souboru",
            "Výchozí pojmenování výstupních souborů překladače",
            "Konfigurace rozhraní příkazového řádku pro překlad",
            "Jak vydat samostatný spustitelný soubor pro Windows",
        ];

        foreach (var answer in answers)
        {
            var title = ChatTitle.Clean(answer, Message);

            title.Should().NotBeNull();
            title!.Length.Should().BeLessThanOrEqualTo(29);
            title.Should().NotEndWith(" ").And.NotContain("…");
        }
    }

    [Fact]
    public void TheInstructionStatesBothBudgets() =>
        ChatTitle.Instruction("Czech")
            .Should().Contain(ChatTitle.MaxWords.ToString()).And.Contain(ChatTitle.MaxLength.ToString());

    [Fact]
    public void ALabelThatIsAlsoAWordIsKept() =>
        ChatTitle.Clean("Shrnutí schůzky s klientem", Message)
            .Should().Be("Shrnutí schůzky s klientem");

    [Fact]
    public void TheInstructionNamesTheTargetLanguage() =>
        ChatTitle.Instruction("Czech").Should().Contain("Czech");

    [Fact]
    public void AnUnknownChatTemplateProducesNoPrompt() =>
        TitlePromptBuilder.Build(Message, "Czech", null).Should().BeNull();

    [Fact]
    public void TheTitlePromptCarriesTheInstructionAndTheMessage()
    {
        var prompt = TitlePromptBuilder.Build(Message, "Czech", ChatTemplate.Gemma);

        prompt.Should().NotBeNull();
        prompt!.Should().Contain("Czech").And.Contain(Message);
        prompt.Should().Contain(ChatTemplate.Gemma.Open);

        // Gemma has no system role, so the instruction folds into the user turn
        // rather than becoming a stray turn of its own.
        prompt.Should().NotContain("<start_of_turn>system");
    }

    [Fact]
    public async Task TheSummaryIsUsedWhenTheModelGivesOne()
    {
        var namer = new ChatNamer(
            (_, _, _, _) => Task.FromResult<string?>("Pojmenování výstupu"),
            (_, _) => throw new Xunit.Sdk.XunitException("tier two must not run when tier one answers"),
            _ => ChatTemplate.Gemma);

        var name = await namer.NameAsync(Ask(), CancellationToken.None);

        name.Should().Be("Pojmenování výstupu");
    }

    [Fact]
    public async Task TheProvisionalNameIsTranslatedWhenTheModelWillNotSummarise()
    {
        // What a translation fine-tune actually does with a summarize
        // instruction: it translates the message instead of naming it.
        var namer = new ChatNamer(
            (_, _, _, _) => Task.FromResult<string?>(Message),
            (_, _) => Task.FromResult(new TranslationOutcome("Výchozí pojmenování výstupu", 7, TimeSpan.Zero)),
            _ => ChatTemplate.Gemma);

        var name = await namer.NameAsync(Ask(), CancellationToken.None);

        name.Should().Be("Výchozí pojmenování výstupu");
    }

    [Fact]
    public async Task NothingIsReturnedWhenBothTiersFail()
    {
        var namer = new ChatNamer(
            (_, _, _, _) => Task.FromResult<string?>(null),
            (_, _) => Task.FromResult(TranslationOutcome.None(TimeSpan.Zero)),
            _ => ChatTemplate.Gemma);

        (await namer.NameAsync(Ask(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ARefusedTranslationDoesNotBecomeAName()
    {
        // A guard refusal returns no text at all, and the chat keeps its
        // truncation rather than being settled on nothing.
        var namer = new ChatNamer(
            (_, _, _, _) => Task.FromResult<string?>("   "),
            (_, _) => Task.FromResult(new TranslationOutcome(null, 0, TimeSpan.Zero)),
            _ => ChatTemplate.Gemma);

        (await namer.NameAsync(Ask(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AComponentNameInATitleSurvives()
    {
        var namer = new ChatNamer(
            (_, _, _, _) => Task.FromResult<string?>("Oprava motoru překladu"),
            (_, _) => Task.FromResult(TranslationOutcome.None(TimeSpan.Zero)),
            _ => ChatTemplate.Gemma);

        var name = await namer.NameAsync(
            Ask() with { Source = "Fix the Engine so translation works." },
            CancellationToken.None);

        name.Should().NotBeNull();
        name!.Should().Contain("Engine").And.NotContain("motor");
    }

    private static ChatNameRequest Ask() => new()
    {
        Source = Message,
        ModelPath = @"C:\models\translategemma-4b-it-Q4_K_M.gguf",
        Direction = TranslationDirection.Between("en", "English", "cs", "Czech"),
        Provisional = "Default output naming: <name>.<to>.<e",
    };
}
