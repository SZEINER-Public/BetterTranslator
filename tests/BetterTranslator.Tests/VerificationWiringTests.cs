using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.Core.Languages;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Core.Verification;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class VerificationWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-verify", Guid.NewGuid().ToString("N"));

    public VerificationWiringTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static TranslationJob Job(string modelPath) => new()
    {
        Text = "The build failed.",
        ModelPath = modelPath,
        Direction = TranslationDirection.Between("en", "English", "cs", "Czech"),
        Effort = TranslationEffort.Simple,
        Temperature = 0.7f,
    };

    [Fact]
    public void TheFactoryYieldsNothingWhenVerificationIsDisabled() =>
        VerificationFactory.Create(new VerificationSettings { Enabled = false }).Should().BeNull();

    [Fact]
    public void TheFactoryYieldsNothingWithoutADictionary() =>
        VerificationFactory.Create(new VerificationSettings { Enabled = true }).Should().BeNull();

    [Fact]
    public void TheFactoryYieldsNothingWhenTheDictionaryPathIsWrong() =>
        VerificationFactory.Create(new VerificationSettings
        {
            Enabled = true,
            HunspellDicPath = Path.Combine(_root, "missing.dic"),
            HunspellAffPath = Path.Combine(_root, "missing.aff"),
        }).Should().BeNull();

    [Fact]
    public void AVerifierIsBuiltFromRealDictionaryFiles()
    {
        var dic = Path.Combine(_root, "cs_CZ.dic");
        var aff = Path.Combine(_root, "cs_CZ.aff");

        File.WriteAllText(aff, "SET UTF-8\n");
        File.WriteAllText(dic, "2\ncením\npomoci\n");

        var verifier = VerificationFactory.Create(new VerificationSettings
        {
            Enabled = true,
            HunspellDicPath = dic,
            HunspellAffPath = aff,
        });

        verifier.Should().NotBeNull();

        var result = verifier!.Verify("I appreciate your help.", "cením pomoci");

        result.Executed.Should().BeTrue();
        result.Spans.Should().HaveCount(2);
        result.HasFindings.Should().BeFalse("both words are in the dictionary");
    }

    [Fact]
    public void TranslateGemmaRequestsAreForcedGreedy()
    {
        var sampling = Job(@"C:\models\translategemma-4b-it-Q6_K.gguf").Sampling();

        sampling.Temperature.Should().Be(0);
        sampling.TopK.Should().Be(1);
        sampling.RepeatPenalty.Should().Be(1.0f);
    }

    [Fact]
    public void OtherModelsKeepTheUserTemperature()
    {
        var sampling = Job(@"C:\models\EuroLLM-9B-Instruct-Q4_K_M.gguf").Sampling();

        sampling.Temperature.Should().Be(0.7f);
    }

    [Fact]
    public void TheRetrySeedStillMovesUnderGreedyDecoding()
    {
        var job = Job(@"C:\models\translategemma-4b-it-Q6_K.gguf");

        job.Sampling(1).Seed.Should().NotBe(job.Sampling().Seed);
    }

    [Fact]
    public void TheOutcomeCarriesTheVerificationResult()
    {
        var outcome = new TranslationOutcome("Cením si tvé pomoci.", 12, TimeSpan.FromSeconds(1))
        {
            Verification = VerificationResult.Skipped("disabled by setting"),
        };

        outcome.Verification.Should().NotBeNull();
        outcome.Verification!.Executed.Should().BeFalse();
        outcome.Text.Should().Be("Cením si tvé pomoci.", "verification never alters the translation");
    }

    [Fact]
    public async Task TheVerificationSettingsSurviveARoundTripThroughTheStore()
    {
        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        var store = new SettingsStore(database);
        var settings = await store.LoadAsync(CancellationToken.None);

        settings.Verification.Enabled.Should().BeTrue();
        settings.Verification.WarningThreshold.Should().Be(60);
        settings.Verification.ErrorThreshold.Should().Be(30);
        settings.Verification.NgramLogProbFloor.Should().Be(-6.0);
        settings.Verification.MinFrequency.Should().Be(1);
        settings.Verification.UntranslatedChunkMinRun.Should().Be(3);
        settings.Verification.HunspellDicPath.Should().BeNull();

        settings.Verification.Enabled = false;
        settings.Verification.HunspellDicPath = @"C:\dict\cs_CZ.dic";
        settings.Verification.WarningThreshold = 55;
        settings.Verification.NgramLogProbFloor = -7.5;

        await store.SaveAsync(settings, CancellationToken.None);

        var reloaded = await store.LoadAsync(CancellationToken.None);

        reloaded.Verification.Enabled.Should().BeFalse();
        reloaded.Verification.HunspellDicPath.Should().Be(@"C:\dict\cs_CZ.dic");
        reloaded.Verification.WarningThreshold.Should().Be(55);
        reloaded.Verification.NgramLogProbFloor.Should().Be(-7.5);
    }

    [Fact]
    public void AnEntryUnderlinesOnlyWhenTheVerifierFoundSomething()
    {
        var entry = new EntryViewModel(new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = Guid.NewGuid(),
            Kind = EntryKind.Sentence,
            Source = "I appreciate your help.",
            Result = "Cenuuji si tvé pomoci.",
            CreatedAt = DateTimeOffset.Now,
            State = EntryState.Done,
            TargetLanguage = "Czech",
        });

        entry.ShowsPlainResult.Should().BeTrue();
        entry.ShowsVerifiedResult.Should().BeFalse("nothing has verified it");

        entry.Verification = new VerificationResult
        {
            Executed = true,
            Spans = [new VerificationSpan { Start = 0, Length = 7, Word = "Cenuuji", Score = 5, Tier = SeverityTier.Error }],
        };

        entry.ShowsVerifiedResult.Should().BeTrue();
        entry.ShowsPlainResult.Should().BeFalse("the two views are exclusive");

        entry.Verification = VerificationResult.Skipped("disabled by setting");

        entry.ShowsVerifiedResult.Should().BeFalse();
        entry.ShowsPlainResult.Should().BeTrue();
    }
}
