using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Languages;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Translating a document with its line count intact.
///
/// Two properties pull against each other. The model needs a SENTENCE -- handed
/// one wrapped line at a time it renders each fragment on its own. The document
/// needs its LINE COUNT back -- a file whose line count changed cannot be written
/// over the original. Every test here is about one or the other.
/// </summary>
public sealed class DocumentTranslatorTests
{
    private static readonly TranslationDirection EnglishToCzech =
        TranslationDirection.Between("en", "English", "cs", "Czech");

    private static TranslationJob JobFor(string text) => new()
    {
        Text = text,
        ModelPath = @"C:\models\translategemma-4b-it.Q4_K_M.gguf",
        Direction = EnglishToCzech,
    };

    /// <summary>
    /// Stands in for the model. Records what it was asked and answers by rule, so
    /// a test states the model's behaviour rather than mocking a transport.
    /// </summary>
    private sealed class FakeModel(Func<string, string?> answer)
    {
        public List<string> Asked { get; } = [];

        public UnitOutcome Translate(TranslationJob job)
        {
            Asked.Add(job.Text);
            var text = answer(job.Text);

            return new UnitOutcome(text, text is null ? 0 : 7, text is null ? "refused" : null);
        }

        public Func<TranslationJob, CancellationToken, Task<UnitOutcome>> Delegate =>
            (job, _) => Task.FromResult(Translate(job));
    }

    /// <summary>A translation that is obviously not the source and wraps the same.</summary>
    private static string Czechify(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => "cz" + w));

    [Fact]
    public async Task TheLineCountIsAlwaysTheSourcesLineCount()
    {
        const string Document = """
            # Layout of the store

            The store resolves a model by name and falls back to
            the pinned revision when the name is ambiguous.

            ```csharp
            var resolved = store.Resolve(name);
            ```

            | Column | Meaning |
            |---|---|
            """;

        var model = new FakeModel(Czechify);

        var result = await new DocumentTranslator(model.Delegate).TranslateAsync(JobFor(Document));

        result.LinesTotal.Should().Be(Document.ReplaceLineEndings("\n").Split('\n').Length);
        result.Text.ReplaceLineEndings("\n").Split('\n').Should().HaveCount(result.LinesTotal);
    }

    [Fact]
    public async Task AWrappedParagraphIsSentAsOneSentenceAndComesBackAsItsOwnLines()
    {
        const string Document = """
            The store resolves a model by name and falls back to
            the pinned revision when the name is ambiguous, which
            is what keeps a run repeatable across machines.
            """;

        var model = new FakeModel(Czechify);

        var result = await new DocumentTranslator(model.Delegate).TranslateAsync(JobFor(Document));

        model.Asked.Should().ContainSingle("three wrapped lines are one sentence, not three");
        model.Asked[0].Should().Contain("falls back to the pinned revision", "the wrap is removed before sending");

        result.ParagraphsJoined.Should().Be(1);
        result.LinesTranslated.Should().Be(3);
        result.Text.ReplaceLineEndings("\n").Split('\n').Should().HaveCount(3);
    }

    [Fact]
    public async Task CodeAndMarkupAreNeverSent()
    {
        const string Document = """
            ```csharp
            var resolved = store.Resolve(name);
            ```
            |---|---|
            """;

        var model = new FakeModel(Czechify);

        var result = await new DocumentTranslator(model.Delegate).TranslateAsync(JobFor(Document));

        model.Asked.Should().BeEmpty();
        result.Text.Should().Be(Document);
        result.LinesUntouched.Should().Be(4);
    }

    [Fact]
    public async Task AParagraphThatCannotBeWrappedToItsLineCountKeepsItsSource()
    {
        // A paragraph that lost a line would fail the line-count check that proves
        // nothing was dropped, and padding it with a blank would split the
        // paragraph in two when the Markdown is rendered.
        const string Document = """
            The store resolves a model by name and falls
            back to the pinned revision when ambiguous.
            """;

        var model = new FakeModel(_ => "jednoslovo");

        var result = await new DocumentTranslator(model.Delegate) { MaxPasses = 1 }
            .TranslateAsync(JobFor(Document));

        result.Text.Should().Be(Document, "refused rather than written short");
        result.LinesTranslated.Should().Be(0);
        result.Refusals.Should().ContainSingle().Which.Should().Contain("unwrappable");
    }

    [Fact]
    public async Task ARefusedLineKeepsItsSourceAndTheReasonIsCounted()
    {
        const string Document = """
            # A heading long enough to be sent

            | Column | Meaning |
            """;

        var model = new FakeModel(_ => null);

        var result = await new DocumentTranslator(model.Delegate) { MaxPasses = 1 }
            .TranslateAsync(JobFor(Document));

        result.Text.Should().Be(Document);
        result.LinesKept.Should().BeGreaterThan(0);
        result.Refusals.Should().NotBeEmpty();
        result.Refusals[0].Should().MatchRegex(@"^\d+ x ");
    }

    [Fact]
    public async Task ALineRefusedOnceIsOfferedAgain()
    {
        // A model handed the same prompt twice does not answer the same way
        // twice, so a line refused on one pass often passes on the next.
        const string Document = "# A heading long enough to be sent";

        var attempts = 0;
        var model = new FakeModel(text =>
        {
            attempts++;
            return attempts == 1 ? null : Czechify(text);
        });

        var result = await new DocumentTranslator(model.Delegate).TranslateAsync(JobFor(Document));

        attempts.Should().Be(2);
        result.LinesTranslated.Should().Be(1);
        result.Text.Should().NotBe(Document);
    }

    [Fact]
    public async Task ALineAlreadyTranslatedIsNotOfferedAgainOnTheNextPass()
    {
        const string Document = """
            # A heading long enough to be sent

            # Another heading long enough to send
            """;

        var model = new FakeModel(text => text.Contains("Another", StringComparison.Ordinal) ? null : Czechify(text));

        var result = await new DocumentTranslator(model.Delegate).TranslateAsync(JobFor(Document));

        model.Asked.Count(a => a.Contains("Another", StringComparison.Ordinal)).Should().Be(2, "refused, so retried");
        model.Asked.Count(a => a.Contains("A heading", StringComparison.Ordinal)).Should().Be(1, "already translated");
    }

    [Fact]
    public async Task ABlockquoteKeepsItsMarkerOnEveryLine()
    {
        // A paragraph that lost the marker from line two onward stops being a
        // blockquote halfway through.
        const string Document = """
            > The store resolves a model by name and falls back to
            > the pinned revision when the name is ambiguous here.
            """;

        var model = new FakeModel(Czechify);

        var result = await new DocumentTranslator(model.Delegate).TranslateAsync(JobFor(Document));

        model.Asked.Should().ContainSingle();
        model.Asked[0].Should().NotContain(">", "the marker is markup, not text to translate");

        result.Text.ReplaceLineEndings("\n").Split('\n').Should().OnlyContain(l => l.StartsWith("> "));
    }

    [Fact]
    public async Task WindowsLineEndingsSurviveTheRoundTrip()
    {
        var document = "# A heading long enough to be sent\r\n\r\n| Column | Meaning |";

        var result = await new DocumentTranslator(new FakeModel(Czechify).Delegate).TranslateAsync(JobFor(document));

        result.Text.Should().Contain("\r\n");
        result.Text.Should().NotContain("\n\n\n");
    }

    [Fact]
    public async Task GlueAgainstAProtectedTermIsRepairedWithoutAskingAnything()
    {
        // "LM Studiobezdedem" -- one unreadable token where the source had two.
        // It loses no markup and invents no word, so every gate passes it, and it
        // is a defect only because the source has a space at that exact point.
        const string Document = "Open the presentation in LM Studio for the details.";

        var model = new FakeModel(_ => "Otevřete prezentaciLM Studio pro podrobnosti.");

        var result = await new DocumentTranslator(model.Delegate) { MaxPasses = 1 }
            .TranslateAsync(JobFor(Document));

        result.SpacesRepaired.Should().Be(1);
        result.Text.Should().Contain("prezentaci LM Studio");
    }

    [Fact]
    public async Task ACancelStopsWithinAUnitAndLeavesTheRestAsSource()
    {
        const string Document = """
            # A heading long enough to be sent

            # Another heading long enough to send

            # A third heading long enough to send
            """;

        using var cancellation = new CancellationTokenSource();

        var seen = 0;
        var translator = new DocumentTranslator((job, _) =>
        {
            if (++seen == 2)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(new UnitOutcome(Czechify(job.Text), 7, null));
        });

        var result = await translator.TranslateAsync(JobFor(Document), null, cancellation.Token);

        result.WasCancelled.Should().BeTrue();
        result.LinesTotal.Should().Be(5, "a stopped run still returns a whole document");
        result.Text.Should().Contain("A third heading", "what was never reached keeps its source");
    }

    [Fact]
    public async Task ProgressCountsRealUnitsAndFinishesOnce()
    {
        const string Document = """
            # A heading long enough to be sent

            # Another heading long enough to send
            """;

        var reports = new List<DocumentProgress>();
        var progress = new SyncProgress<DocumentProgress>();

        await new DocumentTranslator(new FakeModel(Czechify).Delegate)
            .TranslateAsync(JobFor(Document), progress);

        reports.AddRange(progress.Reports);

        reports.Should().NotBeEmpty();
        reports[0].UnitsDone.Should().Be(0, "counters start at zero");
        reports[0].UnitsTotal.Should().Be(2);
        reports.Count(r => r.IsFinished).Should().Be(1);
        reports[^1].LinesTranslated.Should().Be(2);
        reports[^1].Percent.Should().Be(0, "the finishing report is a summary, not a unit tick");
    }

    [Fact]
    public async Task EveryLineIsSentWithNothingCarriedFromTheLineBefore()
    {
        // A hundred sentences in one message are a hundred separate translations.
        // Nothing from line one may reach line two -- no earlier text, no earlier
        // answer, no accumulated instruction. Only the Memory chip adds anything,
        // and it is not attached here.
        const string Document = """
            There are many cultures where cannibalism is encouraged.
            She really didn't like the color pink, but she loved purple.
            I appreciate the opportunity I've had here, and I hope you have a great summer.
            """;

        var jobs = new List<TranslationJob>();

        var translator = new DocumentTranslator((job, _) =>
        {
            jobs.Add(job);
            return Task.FromResult(new UnitOutcome(Czechify(job.Text), 7, null));
        })
        {
            MinLetters = 1,
        };

        await translator.TranslateAsync(JobFor(Document));

        jobs.Should().HaveCount(3);

        var sources = Document.Split('\n');

        for (var i = 0; i < 3; i++)
        {
            jobs[i].Text.Should().Be(sources[i], "the job carries this line and only this line");
            jobs[i].Memory.Should().BeNull("nothing was retrieved, because no chip is attached");
            jobs[i].UseProjectVocabulary.Should().BeFalse();
        }

        // Every job is the same in every respect except its text, which is what
        // "context free" has to mean for it to be checkable.
        jobs.Select(j => j with { Text = string.Empty }).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task AnEmptyDocumentIsNotAnError()
    {
        var result = await new DocumentTranslator(new FakeModel(Czechify).Delegate).TranslateAsync(JobFor(""));

        result.Text.Should().BeEmpty();
        result.LinesTotal.Should().Be(1);
        result.LinesTranslated.Should().Be(0);
    }
}
