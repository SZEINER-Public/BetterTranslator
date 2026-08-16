using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Languages;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Not an assertion suite. This drives a real model with a real send and PRINTS
/// every step -- which prompt shape was used, what came back byte for byte, and
/// which gate refused it -- because a translation that "did not work" can fail at
/// six different points and the entry that keeps its source looks identical for
/// all of them.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class TranslationDiagnosticTests(ITestOutputHelper output)
{
    private const string Source = """
        The fire alarm was broken, but nobody knew.
        It's likely to rain.
        What is that huge building in front of us?
        A big fire broke out after the earthquake.
        His voice was pitchy and weak, partially because he was so nervous.
        Is the room big enough for you?
        I'm one of his fiercest critics.
        Would you like your cash in tens or twenties?
        Mr. White went to the store last night.
        I ask why she's brought tortillas.
        """;

    private static string? ModelPath(string contains) =>
        Directory.EnumerateFiles(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".lmstudio-shared",
                "models"),
            "*.gguf",
            SearchOption.AllDirectories)
        .FirstOrDefault(p => Path.GetFileName(p).Contains(contains, StringComparison.OrdinalIgnoreCase));

    private async Task ReportAsync(string modelContains, Language to)
    {
        var path = ModelPath(modelContains);

        if (path is null)
        {
            output.WriteLine($"no model matching '{modelContains}' on this machine");
            return;
        }

        using var translator = new LocalTranslator();

        var job = new TranslationJob
        {
            Text = Source,
            ModelPath = path,
            Direction = TranslationDirection.Between("en", "English", to.Code, to.Name),
        };

        output.WriteLine($"=== {Path.GetFileName(path)}  ->  {to.Name} ({to.Code}) ===");

        var outcome = await translator.TranslateAsync(job, CancellationToken.None);

        output.WriteLine($"state        : {translator.State} {translator.Reason}");
        output.WriteLine($"prompt from  : {translator.LastPromptSource}");
        output.WriteLine($"guard verdict: {translator.LastGuardVerdict ?? "(accepted)"}");
        output.WriteLine($"tokens       : {outcome.GeneratedTokens} in {outcome.Duration.TotalSeconds:0.0}s");
        output.WriteLine("--- answer ---");
        output.WriteLine(outcome.Text ?? "(nothing came back)");
        output.WriteLine(string.Empty);
    }

    [ModelFact]
    public async Task WhatHappensSendingTenEnglishLines()
    {
        await ReportAsync("translategemma-4b-it.Q4_K_M", new Language("cs", "Czech"));
        await ReportAsync("translategemma-4b-it.Q4_K_M", new Language("en", "English"));
    }

    /// <summary>
    /// The same text one line at a time, which is what the document path does.
    /// Ten short sends behave very differently from one ten-line send, and which
    /// of the two is failing is the first thing worth knowing.
    /// </summary>
    [ModelFact]
    public async Task WhatHappensLineByLine()
    {
        var path = ModelPath("translategemma-4b-it.Q4_K_M");

        if (path is null)
        {
            return;
        }

        using var translator = new LocalTranslator();

        foreach (var line in Source.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var outcome = await translator.TranslateAsync(
                new TranslationJob
                {
                    Text = line,
                    ModelPath = path,
                    Direction = TranslationDirection.Between("en", "English", "cs", "Czech"),
                },
                CancellationToken.None);

            output.WriteLine($"[{translator.LastGuardVerdict ?? "ok"}] {line}");
            output.WriteLine($"    -> {outcome.Text ?? "(nothing)"}");
        }
    }

    /// <summary>The prompt itself, with no model involved.</summary>
    [Fact]
    public void WhatPromptTheseTenLinesProduce()
    {
        foreach (var (label, code, name) in new[]
        {
            ("Czech", "cs", "Czech"),
            ("English", "en", "English"),
        })
        {
            var built = BetterTranslator.Engine.Models.TranslationPromptBuilder.Build(
                ModelPath("translategemma-4b-it.Q4_K_M") ?? "translategemma-4b-it.gguf",
                Source,
                "English",
                "en",
                name,
                code,
                preamble: null,
                rulesText: BetterTranslator.Engine.Slop.RagConfig.For(code).RulesText);

            output.WriteLine($"=== target {label} ===");
            output.WriteLine($"template    : {built?.TemplateName}");
            output.WriteLine($"instruction : {built?.InstructionSource}");
            output.WriteLine("--- prompt ---");
            output.WriteLine(built?.Text ?? "(none; the runtime's own prompt would be used)");
            output.WriteLine(string.Empty);
        }
    }
}
