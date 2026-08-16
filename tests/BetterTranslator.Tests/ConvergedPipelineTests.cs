using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// The window and the agent surfaces are meant to translate the same way. These
/// pin the parts that used to differ silently -- what reaches the model, and how
/// the text is cut up before it gets there -- by looking at the jobs the engine
/// was actually handed.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class ConvergedPipelineTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-converged", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheJobFactoryCarriesEverySettingTheWindowUsedToCarryAlone()
    {
        var settings = new AppSettings
        {
            Temperature = 0.55,
            Instruction = "Keep product names in English.",
            UseDomainVocabulary = false,
            Effort = TranslationEffort.Thinking,
        };

        var job = TranslationJobs.For(
            new TranslationRequest("The build is green.", Direction(), @"C:\models\m.gguf")
            {
                Memory = "earlier: build -> sestavení",
                UsesMemory = true,
            },
            settings);

        job.Temperature.Should().BeApproximately(0.55f, 0.0001f, "the slider is stored now, so an agent can send it");
        job.Instruction.Should().Be("Keep product names in English.");
        job.Memory.Should().Be("earlier: build -> sestavení");
        job.UseProjectVocabulary.Should().BeTrue("asking for this project's knowledge is what turns the glossary on");
        job.UseDomainVocabulary.Should().BeFalse();
        job.Effort.Should().Be(TranslationEffort.Thinking);
    }

    [Fact]
    public void AnEmptyInstructionIsNullRatherThanAnEmptyLineInThePrompt()
    {
        var job = TranslationJobs.For(
            new TranslationRequest("x", Direction(), @"C:\models\m.gguf"),
            new AppSettings { Instruction = "   " });

        job.Instruction.Should().BeNull();
        job.IsGrounded.Should().BeFalse("nothing but the text reaches the model");
    }

    [Fact]
    public async Task AMultiLineRequestIsCutUpTheWayTheWindowCutsItUp()
    {
        var engine = new StubEngine(job => new EngineAnswer("PŘELOŽENO: " + job.Text, null, 3, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);

        var result = await gateway.TranslateTextAsync(
            "The build is green.\nAll tests pass.\nShipping today.",
            "en",
            "cs",
            usesMemory: false,
            CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error?.Message);

        foreach (var job in engine.Asked)
        {
            output.WriteLine(job.Text);
        }

        engine.Asked.Should().HaveCount(3, "one call per line, which is what bounds the damage a bad line can do");
        engine.Asked.Select(j => j.Text).Should().NotContain(t => t.Contains('\n'));
        result.Text.Should().Contain("PŘELOŽENO").And.Contain("\n");
    }

    [Fact]
    public async Task AskingForMemoryTurnsOnTheProjectGlossaryAndNotAskingLeavesItOff()
    {
        var engine = new StubEngine(job => new EngineAnswer("ok", null, 1, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);

        await gateway.TranslateTextAsync("The build is green.", "en", "cs", usesMemory: false, CancellationToken.None);
        engine.Asked.Single().UseProjectVocabulary.Should().BeFalse("the chip is off by default in the window too");

        engine.Asked.Clear();

        await gateway.TranslateTextAsync("The build is green.", "en", "cs", usesMemory: true, CancellationToken.None);
        engine.Asked.Single().UseProjectVocabulary.Should().BeTrue();
    }

    [Fact]
    public async Task AFileIsRoutedByWhatIsInItRatherThanByItsExtension()
    {
        var engine = new StubEngine(job => new EngineAnswer("\"přeloženo\"", null, 1, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);

        // JSON in a .txt. Routing on the extension sent this to the document
        // pipeline; the window has always read what is actually there.
        var input = Path.Combine(_root, "strings.txt");
        await File.WriteAllTextAsync(input, "{\"greeting\": \"The build is green.\"}");

        var result = await gateway.TranslateFileAsync(
            input,
            output: null,
            from: "en",
            to: "cs",
            overwrite: true,
            progress: null,
            CancellationToken.None);

        result.Status.Should().Be("ok", result.Error);

        var written = await File.ReadAllTextAsync(result.Out!);

        written.Should().StartWith("{", "a resource file comes back as a resource file");
        written.Should().Contain("greeting", "its keys are not translated");
    }

    [Fact]
    public async Task AnAgentTranslationBecomesARowTheWindowCanRender()
    {
        var engine = new StubEngine(job => new EngineAnswer("Sestavení je zelené.", null, 5, TimeSpan.FromMilliseconds(90)));
        var gateway = await GatewayAsync(engine);
        var store = new ChatStore(new Database(new AppPaths(_root)));

        var result = await gateway.TranslateTextAsync(
            "The build is green.",
            "en",
            "cs",
            usesMemory: false,
            CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error?.Message);
        result.EntryId.Should().NotBeNullOrEmpty("the caller has to be able to reach the row again");

        var entry = await store.GetEntryAsync(Guid.Parse(result.EntryId!), CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.Source.Should().Be("The build is green.");
        entry.Result.Should().Be("Sestavení je zelené.");
        entry.State.Should().Be(EntryState.Done);

        // The three fields that decide how a row renders and how a retry runs.
        entry.TargetLanguage.Should().Be("Czech", "the display name heads the result");
        entry.SourceCode.Should().Be("en");
        entry.TargetCode.Should().Be("cs");
        entry.Kind.Should().Be(EntryKind.Sentence, "it has spaces in it");
        entry.GeneratedTokens.Should().Be(5);

        var chats = await store.GetChatsAsync(CancellationToken.None);

        chats.Should().ContainSingle("one chat per agent session, created when it first has something to hold");

        // Truncated source to begin with, then the model names it once the
        // first translation lands -- the same two steps the window takes.
        chats[0].Name.Should().NotBeNullOrWhiteSpace();
        chats[0].NameIsProvisional.Should().BeFalse("the model that just translated named it");
    }

    [Fact]
    public async Task EverythingOneSessionTranslatesLandsInTheOneChat()
    {
        var engine = new StubEngine(job => new EngineAnswer("přeloženo", null, 1, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);
        var store = new ChatStore(new Database(new AppPaths(_root)));

        await gateway.TranslateTextAsync("First.", "en", "cs", false, CancellationToken.None);
        await gateway.TranslateTextAsync("Second.", "en", "cs", false, CancellationToken.None);

        var chats = await store.GetChatsAsync(CancellationToken.None);
        chats.Should().ContainSingle();

        var entries = await store.GetEntriesAsync(chats[0].Id, CancellationToken.None);
        entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task ARefusedTranslationIsStoredAsFailedRatherThanAsItsOwnSource()
    {
        // The model hands back exactly what it was given, which every gate reads
        // as nothing having been translated.
        var engine = new StubEngine(job => new EngineAnswer(job.Text, null, 1, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);
        var store = new ChatStore(new Database(new AppPaths(_root)));

        await gateway.TranslateTextAsync("The build is green.", "en", "cs", false, CancellationToken.None);

        var chats = await store.GetChatsAsync(CancellationToken.None);
        var entries = await store.GetEntriesAsync(chats[0].Id, CancellationToken.None);

        entries.Should().ContainSingle();
        entries[0].State.Should().Be(EntryState.Failed);
        entries[0].Result.Should().BeEmpty(
            "a result equal to its source is dropped on load, so storing one makes a row that changes on reload");
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    public async Task AFormatTheWindowCanReadIsOneTheAgentCanTranslate(string extension)
    {
        using var files = new Fixtures();

        var engine = new StubEngine(job => new EngineAnswer("PŘELOŽENO", null, 2, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);

        var input = extension == ".pdf" ? files.PdfFile : files.WordFile;

        var result = await gateway.TranslateFileAsync(
            input,
            output: null,
            from: "en",
            to: "cs",
            overwrite: true,
            progress: null,
            CancellationToken.None);

        result.Status.Should().Be("ok", result.Error);

        // Named for what it is. A translation written into a file called .pdf is
        // a file no reader can open, and dropping the extension entirely would
        // collide with the .docx of the same name beside it.
        result.Out.Should().EndWith($"{extension}.cs.txt");
        File.Exists(result.Out!).Should().BeTrue();

        (await File.ReadAllTextAsync(result.Out!)).Should().Contain("PŘELOŽENO");
        engine.Asked.Should().NotBeEmpty("the reader produced text and the text reached the model");
    }

    [Fact]
    public async Task APdfComesBackInLinesRatherThanAPagePerLine()
    {
        Directory.CreateDirectory(_root);

        var path = Fixtures.WritePdf(
            Path.Combine(_root, "three.pdf"),
            "The build is green.",
            "All tests pass.",
            "Shipping today.");

        var content = await new Indexing.Readers.DocumentReaders().ReadAsync(path, CancellationToken.None);

        var lines = content.Text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            output.WriteLine(line);
        }

        lines.Should().HaveCount(3, "a page is not a line; everything downstream works line by line");
        lines[0].Should().Be("The build is green.");
        lines[2].Should().Be("Shipping today.");
    }

    [Fact]
    public async Task ThePdfLinesAreTheUnitsTheModelIsAskedAbout()
    {
        Directory.CreateDirectory(_root);

        var path = Fixtures.WritePdf(
            Path.Combine(_root, "two.pdf"),
            "The build is green.",
            "All tests pass.");

        var engine = new StubEngine(job => new EngineAnswer("PŘELOŽENO", null, 1, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);

        await gateway.TranslateFileAsync(path, null, "en", "cs", true, null, CancellationToken.None);

        engine.Asked.Should().HaveCount(2, "one call per line, not one call for the whole page");
        engine.Asked.Select(j => j.Text).Should().Contain("The build is green.");
    }

    [Fact]
    public void AFormatWithNoReaderIsStillRefusedAndSaysWhatToDo()
    {
        DocumentFormats.IsTranslatable(@"C:\docs\minutes.odt").Should().BeFalse();
        DocumentFormats.IsTranslatable(@"C:\docs\archive.zip").Should().BeFalse();
        DocumentFormats.IsTranslatable(@"C:\docs\terms.pdf").Should().BeTrue();

        // Word leaves these beside a document it has open. Reading one fails
        // inside the reader, and a batch should not stop because of it.
        DocumentFormats.IsTranslatable(@"C:\docs\~$handbook.docx").Should().BeFalse();

        DocumentFormats.UnsupportedMessage(@"C:\docs\minutes.odt")
            .Should().Contain("minutes.odt").And.Contain("Export");
    }

    [Fact]
    public async Task AFileTheReaderCannotMakeSenseOfFailsAsOneFileRatherThanThrowing()
    {
        var engine = new StubEngine(job => new EngineAnswer("PŘELOŽENO", null, 1, TimeSpan.Zero));
        var gateway = await GatewayAsync(engine);

        // Named .pdf and not one. PdfPig throws rather than returning nothing,
        // and in a batch an escaping exception would abort every file after it.
        var broken = Path.Combine(_root, "not-really.pdf");
        await File.WriteAllTextAsync(broken, "This is not a PDF at all.");

        var result = await gateway.TranslateFileAsync(
            broken,
            output: null,
            from: "en",
            to: "cs",
            overwrite: true,
            progress: null,
            CancellationToken.None);

        result.Status.Should().Be("failed", "a file that cannot be read is not a file that was translated");
        result.Error.Should().NotBeNullOrWhiteSpace("and the caller is told what happened to it");
        engine.Asked.Should().BeEmpty("nothing reached the model");
    }

    [Fact]
    public async Task TwoProcessesCanWriteTheSameHistoryWithoutOneWaitingOutTheOther()
    {
        Directory.CreateDirectory(_root);

        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            await using var mode = connection.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode;";

            var journal = (string?)await mode.ExecuteScalarAsync(CancellationToken.None);

            output.WriteLine($"journal_mode = {journal}");
            journal.Should().Be("wal", "a reader must not block the writer when the window and an agent share the file");
        }

        // A reader held open across a write, which under the default rollback
        // journal is exactly the shape that made the writer wait.
        var store = new ChatStore(database);
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = "Shared",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await store.AddChatAsync(chat, CancellationToken.None);

        await using var reading = await database.OpenAsync(CancellationToken.None);
        await using var query = reading.CreateCommand();

        query.CommandText = "SELECT id FROM chats;";

        await using (var reader = await query.ExecuteReaderAsync(CancellationToken.None))
        {
            (await reader.ReadAsync(CancellationToken.None)).Should().BeTrue();

            // Mid-read, from a second connection, the way a second process would.
            var writer = new ChatWriter(new ChatStore(new Database(paths)));

            await writer.BeginAsync(
                chat.Id,
                "The build is green.",
                Direction(),
                DateTimeOffset.UtcNow,
                file: null,
                CancellationToken.None);
        }

        var entries = await store.GetEntriesAsync(chat.Id, CancellationToken.None);
        entries.Should().ContainSingle("the write landed while the read was open");
    }

    private static Core.Languages.TranslationDirection Direction() =>
        Core.Languages.TranslationDirection.Between("en", "English", "cs", "Czech");

    private async Task<TranslationGateway> GatewayAsync(StubEngine engine)
    {
        Directory.CreateDirectory(_root);

        var paths = new AppPaths(_root);
        var database = new Database(paths);

        await database.MigrateAsync(CancellationToken.None);

        return new TranslationGateway(
            database,
            new SettingsStore(database),
            new InstallPaths(paths),
            new AppSettings { SelectedModelPath = ModelFile() },
            engine);
    }

    /// <summary>
    /// A file that exists, because the gateway refuses to translate without a
    /// model on disk. Its contents are never read: the engine is a stub.
    /// </summary>
    private string ModelFile()
    {
        var path = Path.Combine(_root, "stub-model.gguf");

        Directory.CreateDirectory(_root);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, "not a model");
        }

        return path;
    }
}
