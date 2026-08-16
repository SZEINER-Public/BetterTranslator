using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BetterTranslator.Runtime;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Gates a test behind BETTERTRANSLATOR_MODEL_TESTS=1. A plain Trait would not be
/// enough: an unfiltered `dotnet test` runs every trait, and loading a 2.3 GiB model
/// would turn a four-second suite into a minutes-long one. xunit 2.x decides Skip at
/// discovery, so the check has to happen in the attribute rather than in the body.
/// </summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public const string Gate = "BETTERTRANSLATOR_MODEL_TESTS";

    public ModelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Gate) != "1")
        {
            Skip = $"Set {Gate}=1 to run. Loads a 2.3 GiB model from disk.";
        }
    }
}

/// <summary>
/// Loads the model once for the whole class. Two reasons rather than one: a second
/// load would cost another 2.3 GiB and another wait, and br_shutdown is process-wide
/// while the binding's init latch is one-shot -- shutting down between tests would
/// leave the next load running against a torn-down runtime.
/// </summary>
public sealed class BetterRuntimeFixture : IDisposable
{
    public BetterRuntimeFixture()
    {
        ModelPath = FindModel();

        if (ModelPath is null)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        // Explicit, never the model's own maximum. This model reports 131072, which
        // llama-cli turned into a 14.7 GB working set for a 2.3 GB model.
        Model = new BetterRuntimeModel(ModelPath, new ModelParams { NCtx = 4096 });
        LoadElapsed = clock.Elapsed;
    }

    public string? ModelPath { get; }

    public BetterRuntimeModel? Model { get; }

    public TimeSpan LoadElapsed { get; }

    /// <summary>
    /// Globbed, not pasted: the design spec writes the name with a hyphen before
    /// Q4_K_M and the file on disk uses a dot.
    /// </summary>
    private static string? FindModel()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lmstudio-shared",
            "models");

        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateFiles(root, "translategemma-4b-it*Q4_K_M.gguf", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".orig", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        Model?.Dispose();

        if (Model is not null)
        {
            Native.br_shutdown();
        }
    }
}

/// <summary>
/// S10 of the BetterRuntime design: proves the CPU flavour loads in-process, loads a
/// real GGUF and returns a translation. Not the production wiring -- nothing here
/// touches the chat UI or the existing InferenceHost.
/// </summary>
[Trait("Category", "RequiresModel")]
public sealed class BetterRuntimeSmokeTests(BetterRuntimeFixture fixture, ITestOutputHelper output)
    : IClassFixture<BetterRuntimeFixture>
{
    private static readonly Language English = new("en", "English");
    private static readonly Language Czech = new("cs", "Czech");

    [ModelFact]
    public void TheRuntimeLoadsTheModelAndTranslates()
    {
        fixture.ModelPath.Should().NotBeNull("the smoke test needs the translategemma GGUF on disk");
        var model = fixture.Model!;

        output.WriteLine($"br_version:  {BetterRuntimeModel.Version}");
        output.WriteLine($"model:       {fixture.ModelPath}");
        output.WriteLine($"bytes:       {new FileInfo(fixture.ModelPath!).Length:N0}");
        output.WriteLine($"load time:   {fixture.LoadElapsed.TotalSeconds:F2} s");

        BetterRuntimeModel.Version.Should().NotBeNullOrWhiteSpace();

        // The cap took effect, rather than the model's own 131072 silently winning.
        model.ContextSize.Should().Be(4096);

        var prompt = model.BuildTranslatePrompt("Good morning", English, Czech);
        output.WriteLine($"prompt:\n{prompt}");
        prompt.Should().Contain("<start_of_turn>user");
        prompt.Should().Contain("<end_of_turn>");
        prompt.Should().Contain("Czech");
        prompt.Should().Contain("Good morning");

        var clock = Stopwatch.StartNew();
        var translated = model.Translate("Good morning", English, Czech);
        clock.Stop();

        output.WriteLine($"translated:  {translated}");
        output.WriteLine($"gen time:    {clock.Elapsed.TotalSeconds:F2} s");

        translated.Should().NotBeNullOrWhiteSpace();
        translated.Trim().Should().NotBe("Good morning", "the model must translate, not echo");
        BeValidUtf8(translated).Should().BeTrue("mojibake is the failure this runtime exists to avoid");
    }

    [ModelFact]
    public async Task StreamingRebuildsTheSameTextWithDiacriticsIntact()
    {
        fixture.ModelPath.Should().NotBeNull();
        var model = fixture.Model!;

        // Deliberately a phrase whose Czech carries diacritics: the multi-byte
        // codepoints are exactly what a naive per-token emit would split.
        const string Source = "Thank you very much for your help.";

        var chunks = new List<string>();
        await foreach (var chunk in model.TranslateStream(Source, English, Czech))
        {
            chunks.Add(chunk);
        }

        var joined = string.Concat(chunks);
        output.WriteLine($"chunks:      {chunks.Count}");
        output.WriteLine($"streamed:    {joined}");

        joined.Should().NotBeNullOrWhiteSpace();
        BeValidUtf8(joined).Should().BeTrue();
        joined.Should().NotContain("�", "a replacement character means a split codepoint escaped");
    }

    /// <summary>
    /// Drives the raw ABI because the managed wrapper hides the case: br_gen_next may
    /// write zero bytes while out_done is still false, and treating that as
    /// end-of-stream would truncate output mid-word.
    /// </summary>
    [ModelFact]
    public void AZeroLengthChunkDoesNotEndTheStream()
    {
        fixture.ModelPath.Should().NotBeNull();

        var tp = new TranslateParamsNative
        {
            SourceLangCode = English.Code,
            SourceLangName = English.Name,
            TargetLangCode = Czech.Code,
            TargetLangName = Czech.Name,
        };
        var gp = BetterRuntimeModel.DefaultGenParams();

        var handle = IntPtr.Zero;
        var text = new StringBuilder();
        var zeroLengthWhileRunning = 0;
        var calls = 0;

        try
        {
            Native.br_gen_start_translate(fixture.Model!.Handle, "The weather is beautiful today.", ref tp, ref gp, out handle)
                .Should().Be(BrStatus.Ok);

            var buf = new byte[1024];

            while (true)
            {
                var status = Native.br_gen_next(handle, buf, buf.Length, out var len, out var done);
                calls++;

                status.Should().Be(BrStatus.Ok);

                if (len == 0 && !done)
                {
                    zeroLengthWhileRunning++;
                }

                if (len > 0)
                {
                    text.Append(Encoding.UTF8.GetString(buf, 0, len));
                }

                if (done)
                {
                    break;
                }
            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                Native.br_gen_free(handle);
            }
        }

        output.WriteLine($"br_gen_next calls:            {calls}");
        output.WriteLine($"zero-length, not done:        {zeroLengthWhileRunning}");
        output.WriteLine($"raw ABI text:                 {text}");

        // The contract is tolerance, not occurrence: a zero-length chunk must not cut
        // the loop short. Whether one occurs depends on where token boundaries fall.
        text.ToString().Should().NotBeNullOrWhiteSpace(
            "zero-length chunks must not have truncated generation");
        BeValidUtf8(text.ToString()).Should().BeTrue();
    }

    /// <summary>
    /// The free-context guarantee, against the real model rather than against a
    /// stub: the same text sent repeatedly returns the same translation. If the
    /// runtime carried anything between calls -- a KV cache, an earlier turn --
    /// the third answer would drift from the first, and a long chat would
    /// slowly cost more context than a fresh one.
    /// </summary>
    [ModelFact]
    public void RepeatedSendsDoNotAccumulateContext()
    {
        fixture.ModelPath.Should().NotBeNull();
        var model = fixture.Model!;

        var sampling = BetterRuntimeModel.DefaultGenParams();
        sampling.Temperature = 0f;

        var answers = Enumerable.Range(0, 3)
            .Select(_ => model.Translate("The build failed.", English, Czech, sampling).Trim())
            .ToList();

        foreach (var (answer, index) in answers.Select((a, i) => (a, i)))
        {
            output.WriteLine($"send {index + 1}: {answer}");
        }

        answers.Distinct().Should().ContainSingle(
            "each send builds its whole prompt from its own text, so nothing carries between them");
    }

    /// <summary>
    /// The Memory path end to end: the retrieved block reaches the model, and
    /// the model still returns a translation of the text rather than of the
    /// block. Proves the splice lands inside the user turn -- a block placed
    /// outside the turn markers would come back as commentary.
    /// </summary>
    [ModelFact]
    public void AMemoryBlockReachesTheModelWithoutBecomingTheText()
    {
        fixture.ModelPath.Should().NotBeNull();
        var model = fixture.Model!;

        const string Source = "The build failed.";

        var memory = BetterTranslator.Indexing.Retrieval.MemoryContext.Build(
            [new BetterTranslator.Indexing.Retrieval.TranslationPair("The build failed.", "Sestavení selhalo.")],
            [new BetterTranslator.Indexing.Retrieval.MemoryPassage("handbook.md", "In this project 'build' is 'sestavení'.")]);

        memory.Should().NotBeNull();

        var prompt = BetterTranslator.Runtime.Inference.GroundedPrompt.Splice(
            body => model.BuildTranslatePrompt(body, English, Czech), Source, memory, null);

        prompt.Should().NotBeNull("the builder's own prefix is what the block is positioned against");
        output.WriteLine($"grounded prompt:\n{prompt}");

        var sampling = BetterRuntimeModel.DefaultGenParams();
        sampling.Temperature = 0f;

        var grounded = model.Complete(prompt!, sampling).Trim();
        output.WriteLine($"grounded:    {grounded}");

        grounded.Should().NotBeNullOrWhiteSpace();
        BeValidUtf8(grounded).Should().BeTrue();
        grounded.Should().NotContain("handbook.md", "the reference block must not be echoed back");
        grounded.Should().NotContain("<start_of_turn>", "turn markers escaping into the answer means the splice broke the template");
    }

    /// <summary>
    /// The token count Advanced shows, against the real model.
    ///
    /// It has to be a genuine count and it has to cost nothing extra, so it is
    /// taken from the decode loop rather than estimated from the text: the same
    /// prompt must produce the same answer whether it is run in one shot or a
    /// step at a time, and the step-at-a-time run must report a plausible number
    /// of tokens for what came back.
    /// </summary>
    [ModelFact]
    public void CountingTokensDoesNotChangeTheAnswer()
    {
        fixture.ModelPath.Should().NotBeNull();
        var model = fixture.Model!;

        var sampling = BetterRuntimeModel.DefaultGenParams();
        sampling.Temperature = 0f;

        var prompt = model.BuildTranslatePrompt("The weather is beautiful today.", English, Czech);

        var oneShot = model.Complete(prompt, sampling);
        var (stepped, tokens) = model.CompleteCounted(prompt, sampling);

        output.WriteLine($"one shot:  {oneShot.Trim()}");
        output.WriteLine($"stepped:   {stepped.Trim()}  ({tokens} tokens)");

        stepped.Should().Be(oneShot, "counting must not change what the model produces");

        tokens.Should().BeGreaterThan(0, "something was generated, so something was decoded");

        // A token is several characters, never more than the characters
        // themselves. Anything outside that is a miscount rather than a model
        // being terse or verbose.
        tokens.Should().BeLessThanOrEqualTo(stepped.Trim().Length,
            "a decode step cannot produce less than a character on average across a whole answer");
        tokens.Should().BeGreaterThan(stepped.Trim().Length / 20,
            "one token per twenty characters would mean steps were being missed");
    }

    /// <summary>
    /// A .NET string is UTF-16, so the meaningful check is that it survives a UTF-8
    /// round trip unchanged and carries no replacement characters.
    /// </summary>
    private static bool BeValidUtf8(string value)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        try
        {
            return strict.GetString(strict.GetBytes(value)) == value && !value.Contains('�');
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
