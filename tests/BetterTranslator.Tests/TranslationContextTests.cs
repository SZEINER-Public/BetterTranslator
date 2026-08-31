using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Core.Languages;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing.Retrieval;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What reaches the model, and what does not. The rule these hold is that a
/// chat costs the same context on its hundredth send as on its first: nothing
/// accumulates, and the only way anything but the text gets through is the
/// composer's Memory chip.
/// </summary>
public sealed class TranslationContextTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-ctx", Guid.NewGuid().ToString("N"));
    private readonly List<TranslationAsk> _asks = [];
    private ChatWorkspaceViewModel _workspace = null!;
    private ConfirmDialogViewModel? _dialog;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        _workspace = new ChatWorkspaceViewModel(
            new ChatStore(database),
            new ClockService(),
            dialog =>
            {
                _dialog = dialog;
                return dialog;
            })
        {
            Language = TargetLanguage.All[0],

            // Always has something to offer. If context still comes through as
            // null, it is because nothing asked for it -- which is the point.
            BuildMemoryContext = (phrase, pairs) =>
                MemoryContext.Build(pairs, [new MemoryPassage("handbook.md", $"glossary for {phrase}")]),
        };

        _workspace.Translate = (ask, _) =>
        {
            _asks.Add(ask);
            return Task.FromResult(new TranslationOutcome("translated " + ask.Text, 7, TimeSpan.FromMilliseconds(250)));
        };
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The database file may still be held; the temp folder is disposable.
        }

        return Task.CompletedTask;
    }

    private async Task SendAsync(string text)
    {
        _workspace.Draft = text;
        await _workspace.SendCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Deletes through the route a person takes: the row's command raises the
    /// confirmation, and confirming it is what deletes. Calling the private
    /// method directly would skip the wiring most likely to be broken.
    /// </summary>
    private async Task DeleteAsync(ChatRowViewModel row)
    {
        _workspace.ConfirmDeleteChatCommand.Execute(row);

        _dialog.Should().NotBeNull("deleting a chat must ask first");
        _dialog!.ConfirmCommand.Execute(null);

        // The confirm callback starts the delete without awaiting it.
        await Task.Delay(250);
    }

    [Fact]
    public async Task A_chat_without_the_chip_never_carries_context()
    {
        await SendAsync("the build failed");
        await SendAsync("the build succeeded");
        await SendAsync("the build was cancelled");

        _asks.Should().HaveCount(3);
        _asks.Should().OnlyContain(a => a.Memory == null,
            "nothing but the Memory chip may put anything besides the text in front of the model");
    }

    [Fact]
    public async Task The_chip_is_what_turns_context_on()
    {
        await SendAsync("before the chip");
        _asks[0].Memory.Should().BeNull();

        _workspace.AttachMemoryCommand.Execute(null);
        _workspace.UsesMemory.Should().BeTrue();

        await SendAsync("after the chip");
        _asks[1].Memory.Should().NotBeNull();
    }

    [Fact]
    public async Task Taking_the_chip_off_frees_the_context_again()
    {
        _workspace.AttachMemoryCommand.Execute(null);
        await SendAsync("with memory");
        _asks[0].Memory.Should().NotBeNull();

        var chip = _workspace.Attachments.Single(a => a.IsMemory);
        _workspace.RemoveAttachmentCommand.Execute(chip);
        _workspace.UsesMemory.Should().BeFalse();

        await SendAsync("without memory");
        _asks[1].Memory.Should().BeNull("removing the chip has to actually remove the context");
    }

    [Fact]
    public async Task Context_carries_what_this_chat_already_translated()
    {
        _workspace.AttachMemoryCommand.Execute(null);

        await SendAsync("the build failed");
        await SendAsync("the build failed again");

        _asks[1].Memory.Should().Contain("the build failed")
            .And.Contain("translated the build failed",
                "the earlier pair is the wording this chat has already settled on");
    }

    [Fact]
    public async Task What_each_message_cost_is_kept_and_totalled_for_the_chat()
    {
        await SendAsync("first line");
        await SendAsync("second line");

        _workspace.Entries.Should().OnlyContain(e => e.GeneratedTokens == 7 && e.DurationMs == 250);

        _workspace.ChatTokenTotal.Should().Be(14);
        _workspace.ChatDurationMs.Should().Be(500);
        _workspace.HasChatMetrics.Should().BeTrue();
        _workspace.ChatTokenFigure.Should().Be("14 tokens");
        _workspace.ChatDurationFigure.Should().Contain("in this chat");
    }

    [Fact]
    public async Task The_cost_survives_reopening_the_chat()
    {
        // Measured by the call that produced the result, so it has to be written
        // beside it. Kept in the view model only, it was gone on the next launch
        // and Advanced showed nothing under a message that had plainly run.
        await SendAsync("a line worth measuring");

        // Split deliberately: the model object is what the store writes, so if
        // this holds and the reload does not, the defect is in the SQL rather
        // than in the view model.
        var live = _workspace.Entries.Single();
        live.Entry.GeneratedTokens.Should().Be(7, "the view model must write the cost onto the entry it wraps");
        live.Entry.DurationMs.Should().Be(250, "the view model must write the cost onto the entry it wraps");

        var row = _workspace.Rows.Single();
        _workspace.SelectedRow = null;
        _workspace.SelectedRow = row;

        // Selecting a row reloads its entries off the database.
        await Task.Delay(200);

        var reloaded = _workspace.Entries.Single();
        reloaded.GeneratedTokens.Should().Be(7);
        reloaded.DurationMs.Should().Be(250);
        reloaded.HasMetrics.Should().BeTrue();
    }

    [Fact]
    public async Task An_entry_no_model_ran_for_reports_nothing_rather_than_zero()
    {
        // A lookup answered from memory generated no tokens. "0 tokens - 0 ms"
        // would state a measurement that was never taken.
        _workspace.Translate = (ask, _) =>
        {
            _asks.Add(ask);
            return Task.FromResult(TranslationOutcome.None(TimeSpan.Zero));
        };

        await SendAsync("answered without a model");

        var entry = _workspace.Entries.Single();
        entry.GeneratedTokens.Should().BeNull();
        entry.HasMetrics.Should().BeFalse();
        entry.TokenFigure.Should().BeEmpty();
        entry.DurationFigure.Should().BeEmpty();
        entry.RateFigure.Should().BeEmpty("a rate divided out of a null duration would throw");
        _workspace.HasChatMetrics.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_the_open_chat_lands_on_a_new_one()
    {
        await SendAsync("first chat");
        _workspace.NewChatCommand.Execute(null);
        await SendAsync("second chat");

        _workspace.Rows.Should().HaveCount(2);
        var open = _workspace.SelectedRow!;

        await DeleteAsync(open);

        // Not "whichever chat sorts first". Being dropped into an unrelated
        // conversation reads as though the wrong thing was deleted, and the next
        // thing typed would have gone into it.
        _workspace.SelectedRow.Should().BeNull();
        _workspace.Entries.Should().BeEmpty();
        _workspace.Draft.Should().BeEmpty();
        _workspace.HasEntries.Should().BeFalse();

        // The other chat is still there to go back to.
        _workspace.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_a_chat_you_are_not_reading_leaves_yours_alone()
    {
        await SendAsync("first chat");
        var other = _workspace.SelectedRow!;

        _workspace.NewChatCommand.Execute(null);
        await SendAsync("second chat");

        var mine = _workspace.SelectedRow!;

        await DeleteAsync(other);

        _workspace.SelectedRow.Should().BeSameAs(mine, "deleting someone else's row must not move you");
        _workspace.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task An_entry_that_was_never_translated_is_not_offered_as_a_pair()
    {
        // An entry nothing came back for has no result at all. It is not a
        // translation, and a pair of one phrase with itself would be an
        // instruction to leave the text in English.
        _workspace.Translate = (ask, _) =>
        {
            _asks.Add(ask);
            return Task.FromResult(TranslationOutcome.None(TimeSpan.Zero));
        };

        _workspace.LookupMemory = (_, _) => new MemoryLookup([], [], []);
        _workspace.AttachMemoryCommand.Execute(null);

        await SendAsync("untouched wording");
        await SendAsync("second line");

        _asks[1].Memory.Should().NotContain("untouched wording -> untouched wording");
    }
}

/// <summary>
/// Simple and Thinking are two models and two budgets, not one model under two
/// labels, and a job says for itself whether anything besides the text is going
/// to the runtime.
/// </summary>
public sealed class TranslationJobTests
{
    private static TranslationJob Job(TranslationEffort effort) => new()
    {
        Text = "the build failed",
        ModelPath = @"C:\models\whatever.gguf",
        Direction = TranslationDirection.Between("en", "English", "cs", "Czech"),
        Effort = effort,
    };

    [Fact]
    public void Thinking_is_given_more_room_than_Fast()
    {
        var fast = Job(TranslationEffort.Simple).Sampling();
        var thinking = Job(TranslationEffort.Thinking).Sampling();

        thinking.MaxTokens.Should().BeGreaterThan(fast.MaxTokens);

        // Held under the 4096-token context so prompt, block and answer cannot
        // add up past it.
        thinking.MaxTokens.Should().BeLessThan(4096);
    }

    [Fact]
    public void A_composer_unit_gets_the_short_budget_whatever_the_effort_says()
    {
        // The chat cuts a message into lines and sentences before anything is
        // sent, so a composer unit is never the long passage Thinking's budget
        // exists for. Giving it that ceiling only pays for the tokens a model
        // spends past the answer before it stops.
        var chat = (Job(TranslationEffort.Thinking) with { IsStandalone = true }).Sampling();
        var document = Job(TranslationEffort.Thinking).Sampling();

        chat.MaxTokens.Should().Be(Job(TranslationEffort.Simple).Sampling().MaxTokens);
        chat.MaxTokens.Should().BeLessThan(document.MaxTokens);
    }

    [Fact]
    public void A_document_passage_keeps_the_room_Thinking_promises()
    {
        var document = (Job(TranslationEffort.Thinking) with { IsStandalone = false }).Sampling();

        document.MaxTokens.Should().BeGreaterThan(
            (Job(TranslationEffort.Simple) with { IsStandalone = false }).Sampling().MaxTokens,
            "the file path is the one that can legitimately come back long");
    }

    [Fact]
    public void The_users_temperature_reaches_the_runtime()
    {
        var job = Job(TranslationEffort.Simple) with { Temperature = 0.75f };

        job.Sampling().Temperature.Should().Be(0.75f);
    }

    [Fact]
    public void A_plain_job_is_not_grounded()
    {
        Job(TranslationEffort.Simple).IsGrounded.Should().BeFalse();

        // Whitespace is not context. An empty user-prompt field would otherwise
        // move every translation onto the grounded path for nothing.
        (Job(TranslationEffort.Simple) with { Memory = "   ", Instruction = "" })
            .IsGrounded.Should().BeFalse();
    }

    [Fact]
    public void Memory_or_a_standing_instruction_grounds_it()
    {
        (Job(TranslationEffort.Simple) with { Memory = "glossary" }).IsGrounded.Should().BeTrue();
        (Job(TranslationEffort.Simple) with { Instruction = "keep product names" }).IsGrounded.Should().BeTrue();
    }
}

/// <summary>
/// The splice into the runtime's own prompt. Measured against BetterRuntime
/// 0.1.0: the builder puts the body at the tail of the user turn, and
/// br_complete sends what it is given verbatim.
/// </summary>
public sealed class GroundedPromptTests
{
    /// <summary>Stands in for br_build_translate_prompt, same shape as measured.</summary>
    private static string Build(string body) =>
        "<start_of_turn>user\nYou are a professional translator. "
        + "Please translate the following English text into Czech:\n\n\n"
        + body + "<end_of_turn>\n<start_of_turn>model\n";

    [Fact]
    public void The_block_goes_before_the_instruction_not_before_the_body()
    {
        var spliced = GroundedPrompt.Splice(Build, "the build failed", "build = sestavení", null);

        spliced.Should().NotBeNull();

        // The instruction ends "translate the following text", so a block after
        // it is the following text. Measured: the model translated the block.
        spliced!.IndexOf("sestavení", StringComparison.Ordinal)
            .Should().BeLessThan(spliced.IndexOf("Please translate the following", StringComparison.Ordinal));

        spliced.Should().StartWith("<start_of_turn>user\n", "the block belongs inside the turn, not in front of it");
        spliced.Should().EndWith("<start_of_turn>model\n", "the turn markers must survive the splice");
    }

    [Fact]
    public void Nothing_to_add_leaves_the_prompt_exactly_as_built()
    {
        GroundedPrompt.Splice(Build, "the build failed", null, null)
            .Should().Be(Build("the build failed"));
    }

    [Fact]
    public void A_builder_with_no_invariant_prefix_refuses_rather_than_guesses()
    {
        // Nothing shared from the first character on: there is no position that
        // can be called "before the instruction", so null sends the caller back
        // to the plain translation instead of splicing at a made-up offset.
        var calls = 0;
        string Unstable(string body) => $"{calls++}<turn>\ntranslate:\n{body}<end>";

        GroundedPrompt.Splice(Unstable, "the build failed", "glossary", null).Should().BeNull();
    }

    [Fact]
    public void A_template_with_no_opening_marker_takes_the_block_at_the_front()
    {
        var spliced = GroundedPrompt.Splice(body => "Translate:\n" + body, "text", "glossary", null);

        spliced.Should().NotBeNull();
        spliced!.Should().StartWith("Here is how this project");
    }
}

/// <summary>
/// The block itself: what it holds, what it leaves out, and the cap that keeps
/// a well-indexed project from pushing the text out of its own context window.
/// </summary>
public sealed class MemoryContextTests
{
    [Fact]
    public void Nothing_remembered_is_null_rather_than_an_empty_heading()
    {
        MemoryContext.Build([], []).Should().BeNull();
    }

    [Fact]
    public void Chat_pairs_come_before_indexed_files()
    {
        var block = MemoryContext.Build(
            [new TranslationPair("the build failed", "sestavení selhalo")],
            [new MemoryPassage("handbook.md", "build means sestavení")]);

        block.Should().NotBeNull();
        block!.IndexOf("Earlier in this chat", StringComparison.Ordinal)
            .Should().BeLessThan(block.IndexOf("From indexed files", StringComparison.Ordinal));
    }

    [Fact]
    public void The_newest_pair_leads()
    {
        var block = MemoryContext.Build(
            [new TranslationPair("first", "prvni"), new TranslationPair("second", "druhy")],
            []);

        block!.IndexOf("second", StringComparison.Ordinal)
            .Should().BeLessThan(block.IndexOf("first", StringComparison.Ordinal));
    }

    [Fact]
    public void A_large_index_cannot_spend_more_than_its_budget()
    {
        var passages = Enumerable.Range(0, 500)
            .Select(i => new MemoryPassage($"file{i}.md", new string('x', 900)))
            .ToList();

        var pairs = Enumerable.Range(0, 500)
            .Select(i => new TranslationPair(new string('a', 300), new string('b', 300)))
            .ToList();

        var block = MemoryContext.Build(pairs, passages);

        block!.Length.Should().BeLessThanOrEqualTo(MemoryContext.BudgetCharacters);
    }

    [Fact]
    public void A_chunk_carrying_newlines_stays_one_entry()
    {
        var block = MemoryContext.Build([], [new MemoryPassage("notes.md", "first line\r\nsecond line")]);

        block.Should().Contain("first line second line");
    }
}

/// <summary>
/// Finding a catalogue model on disk. The store path is not the catalogue id --
/// a model sits under whatever publisher folder its source uses.
/// </summary>
public sealed class ModelMatchTests
{
    [Fact]
    public void A_model_is_found_by_its_file_name_wherever_it_landed()
    {
        var library = new ModelLibrary();

        var found = new[]
        {
            new LocalModel(@"C:\store\lmstudio-community\EuroLLM-9B-Instruct-Q4_K_M.gguf", "EuroLLM-9B-Instruct-Q4_K_M", 1),
            new LocalModel(@"C:\store\someone-else\translategemma-4b-it.Q4_K_M.gguf", "translategemma-4b-it.Q4_K_M", 1),
        };

        library.Match(found, "translategemma-4b-it.Q4_K_M.gguf")!.Path
            .Should().Be(@"C:\store\someone-else\translategemma-4b-it.Q4_K_M.gguf");

        library.Match(found, "not-installed.gguf").Should().BeNull();
        library.Match(found, "").Should().BeNull();
    }
}
