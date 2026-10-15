using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Cli;
using BetterTranslator.Runtime.Agents.Mcp;
using BetterTranslator.Runtime.Speech;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RecognitionBindingTests
{
    [Fact]
    public void ManagedRecognitionLayoutsMatchTheNativeAbi()
    {
        Marshal.SizeOf<BsSubstitution>().Should().Be(64);
        Marshal.SizeOf<BsVadSettings>().Should().Be(24);
        Marshal.SizeOf<BsRecEngineConfig>().Should().Be(24);
        Marshal.SizeOf<BsRecCapabilities>().Should().Be(48);
        Marshal.SizeOf<BsRecRequest>().Should().Be(88);
        Marshal.SizeOf<BsRecEvent>().Should().Be(96);
        Marshal.SizeOf<BsRecStats>().Should().Be(160);

        Marshal.OffsetOf<BsSubstitution>(nameof(BsSubstitution.RawOffset)).ToInt32().Should().Be(16);
        Marshal.OffsetOf<BsSubstitution>(nameof(BsSubstitution.Replacement)).ToInt32().Should().Be(48);
        Marshal.OffsetOf<BsRecRequest>(nameof(BsRecRequest.Language)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<BsRecRequest>(nameof(BsRecRequest.FilePath)).ToInt32().Should().Be(24);
        Marshal.OffsetOf<BsRecRequest>(nameof(BsRecRequest.MinConfidence)).ToInt32().Should().Be(52);
        Marshal.OffsetOf<BsRecRequest>(nameof(BsRecRequest.Vad)).ToInt32().Should().Be(64);
        Marshal.OffsetOf<BsRecEvent>(nameof(BsRecEvent.StartSample)).ToInt32().Should().Be(24);
        Marshal.OffsetOf<BsRecEvent>(nameof(BsRecEvent.MinConfidence)).ToInt32().Should().Be(40);
        Marshal.OffsetOf<BsRecEvent>(nameof(BsRecEvent.Text)).ToInt32().Should().Be(48);
        Marshal.OffsetOf<BsRecEvent>(nameof(BsRecEvent.RawLength)).ToInt32().Should().Be(88);
        Marshal.OffsetOf<BsRecStats>(nameof(BsRecStats.InputFrames)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<BsRecStats>(nameof(BsRecStats.CancelLatencyMicros)).ToInt32().Should().Be(152);
    }

    [Fact]
    public void TranscribeParsesItsFileLanguageAndMode()
    {
        var command = CommandLine.Parse(["transcribe", "--file", "a.wav", "--language", "en", "--code-dictation", "--json"]);

        command.IsValid.Should().BeTrue(command.Error);
        command.Verb.Should().Be(Verb.Transcribe);
        command.File.Should().Be("a.wav");
        command.Language.Should().Be("en");
        command.CodeDictation.Should().BeTrue();
        command.Json.Should().BeTrue();

        CommandLine.Parse(["transcribe", "--file=a.wav", "--language=cs"]).CodeDictation.Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { "transcribe", "--language", "en" }, "--file is required")]
    [InlineData(new[] { "transcribe", "--file", "a.wav" }, "--language is required")]
    [InlineData(new[] { "transcribe", "--file", "a.wav", "--language", "en", "--to", "cs" }, "takes --file")]
    [InlineData(new[] { "transcribe", "a.wav", "--language", "en" }, "no free arguments")]
    [InlineData(new[] { "translate", "hi", "--to", "cs", "--code-dictation" }, "belong to bt transcribe")]
    [InlineData(new[] { "translate", "hi", "--to", "cs", "--language", "en" }, "belong to bt transcribe")]
    public void TranscribeRefusesWhatItCannotHonour(string[] args, string expected)
    {
        var command = CommandLine.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Contain(expected);
    }

    [Fact]
    public void TranscriptionFaultsMapToTheDocumentedExitCodes()
    {
        ExitCode.For(TranscriptionFault.None).Should().Be(0);
        ExitCode.For(TranscriptionFault.InvalidRequest).Should().Be(2);
        ExitCode.For(TranscriptionFault.InputMissing).Should().Be(3);
        ExitCode.For(TranscriptionFault.Unavailable).Should().Be(4);
        ExitCode.For(TranscriptionFault.Failed).Should().Be(6);
        ExitCode.For(TranscriptionFault.Cancelled).Should().Be(6);

        Help.ExitCodes.Should().Contain("6  transcription failed");
        Help.Root.Should().Contain("transcribe");
        Help.For(Verb.Transcribe).Should().Contain("USAGE").And.Contain("EXAMPLES").And.Contain("EXIT CODES");
        Help.For(Verb.Transcribe).Should().Contain("--code-dictation");
        Help.Mcp.Should().Contain("eleven tools");
    }

    [Fact]
    public void TheTranscribeToolIsAllowlisted()
    {
        BetterTranslatorTools.Names.Should().Contain("transcribe_file");
        BetterTranslatorTools.Names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task AMissingRecordingIsInputMissingBeforeAnythingLoads()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bt-transcribe-missing.wav");
        File.Delete(missing);

        var result = await FileTranscriber.TranscribeAsync(missing, "en", DictationMode.Prose);

        result.Ok.Should().BeFalse();
        result.Fault.Should().Be(TranscriptionFault.InputMissing);
        result.Error.Should().Contain(missing);
    }

    [Fact]
    public async Task ARecordingWithoutAModelIsUnavailableWithAReason()
    {
        var path = Path.Combine(Path.GetTempPath(), "bt-transcribe-present.wav");
        File.WriteAllBytes(path, new byte[64]);

        var result = await FileTranscriber.TranscribeAsync(path, "en", DictationMode.Code);

        result.Ok.Should().BeFalse();
        result.Fault.Should().Be(TranscriptionFault.Unavailable);
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task ANonWavFileIsRefusedByName()
    {
        var path = Path.Combine(Path.GetTempPath(), "bt-transcribe-notes.mp3");
        File.WriteAllBytes(path, new byte[64]);

        var result = await FileTranscriber.TranscribeAsync(path, "en", DictationMode.Prose);

        result.Fault.Should().Be(TranscriptionFault.InvalidRequest);
        result.Error.Should().Contain(".wav");
    }

    [Fact]
    public void ADisabledDictationButtonCarriesItsReason()
    {
        var dictation = new DictationViewModel();

        dictation.IsAvailable.Should().BeFalse("no recognition model can be attached in this build");
        dictation.ButtonToolTip.Should().Be(dictation.UnavailableReason);
        dictation.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        dictation.IsListening.Should().BeFalse();
        dictation.HasProvisional.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleOnAnUnavailableDictationDoesNothing()
    {
        var dictation = new DictationViewModel();
        var inserted = false;
        dictation.InsertRequested += (_, _) => inserted = true;

        await dictation.ToggleCommand.ExecuteAsync(null);

        dictation.IsListening.Should().BeFalse();
        inserted.Should().BeFalse();
    }

    [Theory]
    [InlineData("", 0, 0, "hello", "hello", 5)]
    [InlineData("say", 3, 0, "hello", "say hello", 9)]
    [InlineData("say ", 4, 0, "hello", "say hello", 9)]
    [InlineData("ab", 1, 0, "X", "a X b", 4)]
    [InlineData("run now", 3, 0, "--amend", "run --amend now", 11)]
    [InlineData("call .", 5, 0, "loadModelAsync", "call loadModelAsync.", 19)]
    [InlineData("replace THIS here", 8, 4, "that", "replace that here", 12)]
    [InlineData("tail", 99, 0, "end", "tail end", 8)]
    public void DictationInsertsAtTheCaretWithSpacing(string draft, int start, int length, string text, string expected, int caret)
    {
        var insertion = DictationViewModel.Insert(draft, start, length, text);

        insertion.Draft.Should().Be(expected);
        insertion.Caret.Should().Be(caret);
    }

    [Fact]
    public void InsertingNothingLeavesTheDraftAlone()
    {
        DictationViewModel.Insert("keep", 2, 0, string.Empty).Should().Be(new DictationInsertion("keep", 2));
    }

    [Fact]
    public void ShippedVocabularyCarriesTheAgentToolNames()
    {
        var terms = TechnicalVocabulary.ShippedTerms(new VocabularySources(DoNotTranslate: false, DomainTerms: false));

        terms.Select(t => t.Term).Should().Contain("transcribe_file").And.Contain("translate_text");
        terms.Should().OnlyContain(t => t.Source == "agent-tools");
        TechnicalVocabulary.ShippedTerms(new VocabularySources(false, false, false, false)).Should().BeEmpty();
    }

    [Fact]
    public void TheJsonEnvelopeCarriesSegmentsAndSubstitutions()
    {
        var transcription = new Transcription
        {
            Ok = true,
            Path = "C:\\rec.wav",
            Language = "en",
            Mode = DictationMode.Code,
            Text = "loadModelAsync",
            Segments =
            [
                new TranscribedSegment(
                    0,
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(1700),
                    "loadModelAsync",
                    "camel case load model async",
                    0.9f,
                    [new DictationSubstitution(SubstitutionKind.Grammar, true, 0.9f, 0, 27, 0, 14, "loadModelAsync")]),
            ],
        };

        using var document = JsonDocument.Parse(Envelope.Transcription(transcription));
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().Should().BeTrue();
        root.GetProperty("mode").GetString().Should().Be("code");
        root.GetProperty("result").GetString().Should().Be("loadModelAsync");
        var segment = root.GetProperty("segments")[0];
        segment.GetProperty("start_ms").GetInt64().Should().Be(500);
        segment.GetProperty("raw").GetString().Should().Be("camel case load model async");
        segment.GetProperty("substitutions")[0].GetProperty("kind").GetString().Should().Be("grammar");
    }

    [SpeechNativeFact]
    public void EverySpokenPairBecomesCodeAndProseLeavesThemAlone()
    {
        using var lexicon = SpeechLexicon.Create(256);
        lexicon.Harvest("BetterTranslator runs GGUF models through llama.cpp on .NET 10. Locale strings live in "
                        + "i18n.json, and loadModelAsync reads max_new_tokens. Fix it with git commit --amend.");
        lexicon.Freeze();

        var pairs = new (string Spoken, string Written)[]
        {
            ("dash dash amend", "--amend"),
            ("dot net", ".NET"),
            ("llama dot cpp", "llama.cpp"),
            ("i eighteen n dot json", "i18n.json"),
            ("camel case load model async", "loadModelAsync"),
            ("snake case max new tokens", "max_new_tokens"),
            ("open paren close paren", "()"),
            ("G G U F", "GGUF"),
        };

        foreach (var (spoken, written) in pairs)
        {
            Dictation.Apply(spoken, DictationMode.Code, lexicon).Text.Should().Be(written, spoken);
            Dictation.Apply(spoken, DictationMode.Prose, lexicon).Text.Should().Be(spoken);
        }

        Dictation.Apply("we convert gguf models on .net", DictationMode.Prose, lexicon).Text
            .Should().Be("we convert GGUF models on .NET");
    }

    [SpeechNativeFact]
    public void SubstitutionSpansAreUtf16IndicesIntoTheText()
    {
        var result = Dictation.Apply("řekni camel case load model async", DictationMode.Code);

        result.Text.Should().Be("řekni loadModelAsync");
        result.Raw.Should().Be("řekni camel case load model async");
        var substitution = result.Substitutions.Should().ContainSingle().Which;
        substitution.Applied.Should().BeTrue();
        result.Text.Substring(substitution.TextStart, substitution.TextLength).Should().Be("loadModelAsync");
        result.Raw.Substring(substitution.RawStart, substitution.RawLength).Should().Be("camel case load model async");
    }

    [SpeechNativeFact]
    public void ARecognitionEngineWithoutAModelSaysSo()
    {
        using var engine = RecognitionEngine.Create();

        engine.Capabilities.Available.Should().BeFalse();
        engine.UnavailableReason.Should().NotBeNullOrWhiteSpace();

        var act = () => engine.Start(new RecognitionRequest { Language = "en", InputSampleRate = 48000 });

        act.Should().Throw<SpeechException>().Which.Status.Should().Be(SpeechStatus.Unavailable);
    }

    [SpeechNativeFact]
    public void TheLexiconRefusesTermsOnceFrozen()
    {
        using var lexicon = SpeechLexicon.Create(16);
        lexicon.Add("GGUF").Should().BeTrue();
        lexicon.Add(string.Empty).Should().BeFalse();
        lexicon.Freeze();

        lexicon.Terms().Should().Equal("GGUF");
        lexicon.Version.Should().NotBe(0UL);

        var act = () => lexicon.Add("late");

        act.Should().Throw<SpeechException>().Which.Status.Should().Be(SpeechStatus.State);
    }
}
