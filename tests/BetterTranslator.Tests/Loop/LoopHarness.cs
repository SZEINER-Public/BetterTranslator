using System.Diagnostics;
using System.IO;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;

namespace BetterTranslator.Tests.Loop;

public sealed record RunReport(
    DocumentUnderTest Candidate,
    TimeSpan Elapsed,
    int Requests,
    int GeneratedTokens,
    int RuntimeFailures,
    int BlocksTranslated,
    int BlocksRecovered,
    int BlocksKept)
{
    public double CharactersPerSecond(int sourceCharacters) =>
        Elapsed.TotalSeconds <= 0 ? 0 : sourceCharacters / Elapsed.TotalSeconds;

    public double TokensPerUnit => Requests == 0 ? 0 : (double)GeneratedTokens / Requests;
}

public sealed class LoopHarness : IDisposable
{
    private const int MaxRuntimeAttempts = 3;

    private static readonly BetterTranslator.Core.Languages.TranslationDirection EnglishToCzech =
        BetterTranslator.Core.Languages.TranslationDirection.Of(
            BetterTranslator.Core.Languages.LanguageChoice.Of("en", "English"),
            BetterTranslator.Core.Languages.LanguageChoice.Of("cs", "Czech"));

    private readonly LocalTranslator _translator = new();

    public LoopHarness(string modelPath, string sourcePath)
    {
        ModelPath = modelPath;
        SourceHadByteOrderMark = BetterTranslator.Engine.Text.DocumentEncoding.ByteOrderMarkAt(sourcePath);
    }

    public string ModelPath { get; }

    public bool SourceHadByteOrderMark { get; }

    public static string? ResolveModel()
    {
        var named = Environment.GetEnvironmentVariable("BT_LOOP_MODEL");

        if (!string.IsNullOrWhiteSpace(named) && File.Exists(named))
        {
            return named;
        }

        foreach (var folder in ModelFolders())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var found = Directory
                .EnumerateFiles(folder, "translategemma-4b-it*Q4_K_M.gguf", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".orig", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> ModelFolders()
    {
        yield return new AppPaths().ModelsFolder;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lmstudio-shared",
            "models");
    }

    public static void PreferInstalledBackend()
    {
        var folder = new AppPaths().ModelsFolder;

        BackendCatalog.SearchAlso(folder);

        var backend = File.Exists(Path.Combine(folder, BackendCatalog.FileNameFor(RuntimeBackend.Cuda)))
            ? RuntimeBackend.Cuda
            : File.Exists(Path.Combine(folder, BackendCatalog.FileNameFor(RuntimeBackend.Vulkan)))
                ? RuntimeBackend.Vulkan
                : RuntimeBackend.Cpu;

        LocalTranslator.Prefer(backend);
    }

    public async Task<RunReport> RunAsync(CorpusSlice slice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var requests = 0;
        var tokens = 0;
        var failures = 0;

        var clock = Stopwatch.StartNew();

        var document = await MarkdownTranslation.TranslateAsync(
            slice.Document.Text,
            async (text, token) =>
            {
                requests++;

                for (var attempt = 0; attempt < MaxRuntimeAttempts; attempt++)
                {
                    var job = new TranslationJob
                    {
                        Text = text,
                        ModelPath = ModelPath,
                        Direction = EnglishToCzech,
                        Effort = TranslationEffort.Fast,
                        IsStandalone = false,
                        UseDomainVocabulary = true,
                        UseProjectVocabulary = false,
                    };

                    try
                    {
                        var outcome = await _translator.TranslateAsync(job, token).ConfigureAwait(false);

                        tokens += outcome.GeneratedTokens;

                        if (_translator.State != TranslatorState.Failed)
                        {
                            return outcome.Text;
                        }
                    }
                    catch (BetterRuntimeException)
                    {
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
                }

                failures++;
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        clock.Stop();

        var candidate = new DocumentUnderTest(
            document.Text,
            BetterTranslator.Engine.Text.DocumentEncoding.EmitsByteOrderMark(SourceHadByteOrderMark),
            slice.Document.Newline);

        return new RunReport(
            candidate,
            clock.Elapsed,
            requests,
            tokens,
            failures,
            document.Translated,
            document.Recovered,
            document.Kept);
    }

    public Task<bool> WarmAsync(CancellationToken cancellationToken) =>
        _translator.EnsureLoadedAsync(ModelPath, cancellationToken);

    public string? LoadFailure => _translator.Reason;

    public void Dispose() => _translator.Dispose();
}
