using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Signals;
using BetterTranslator.Runtime.Verification;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class TranslationVerifierTests
{
    private static readonly string[] CzechWords =
    [
        "cením", "si", "tvé", "pomoci", "jistý", "hráč", "získal", "toto", "je", "test", "vakuum",
    ];

    private static readonly VerificationSettings Settings = new() { NgramLogProbFloor = -100 };

    private static FrequencyLexicon Lexicon() => FrequencyLexicon.FromPairs(CzechWords.Select(w => (w, 100L)));

    private static TranslationVerifier Verifier()
    {
        var lexicon = Lexicon();

        return new TranslationVerifier(
            new FakeSpellChecker(CzechWords),
            new FakeAnalyzer([.. CzechWords, "puuma"]),
            lexicon,
            new CharNgramScorer(lexicon),
            Settings);
    }

    [Fact]
    public void LegalityFlagsTheDoubledVowelInCenuuji() =>
        CharLegalityCheck.Check("Cenuuji").Should().NotBeNull();

    [Fact]
    public void LegalityPassesCenim() =>
        CharLegalityCheck.Check("cením").Should().BeNull();

    [Fact]
    public void LegalityFlagsVakuumWhichStageTwoRescues() =>
        CharLegalityCheck.Check("vakuum").Should().NotBeNull();

    [Fact]
    public void FusedBoundaryIsFoundInJistyBubble() =>
        FusedTokenCheck.HasCamelBoundary("jistýBubble").Should().BeTrue();

    [Fact]
    public void NoFusedBoundaryInBubble() =>
        FusedTokenCheck.HasCamelBoundary("Bubble").Should().BeFalse();

    [Fact]
    public void NgramRanksASeenBigramWordAboveAnUnseenOne()
    {
        var ngram = new CharNgramScorer(Lexicon());

        ngram.AverageLogProb("cením").Should().BeGreaterThan(ngram.AverageLogProb("cenuuji"));
    }

    [Fact]
    public void AStandalonePreservedTokenIsExemptAndAFusedOneIsNot()
    {
        var exemption = new SourceSpanExemption("A certain Bubble appears in Bubble Coin.");

        exemption.IsExempt("Bubble").Should().BeTrue();
        exemption.IsExempt("jistýBubble").Should().BeFalse();
    }

    [Fact]
    public void CenuujiIsConfirmedMalformedAtScoreFive()
    {
        var result = Verifier().Verify("I appreciate your help.", "Cenuuji si tvé pomoci.");
        var cenuuji = result.Spans.First(s => s.Word == "Cenuuji");

        cenuuji.Score.Should().Be(5);
        cenuuji.Tier.Should().Be(SeverityTier.Error);
        cenuuji.Score.Should().BeLessThan(Settings.WarningThreshold);
        cenuuji.Defect.Should().Be(DefectClass.MalformedForm);

        result.Spans.Where(s => s.Word != "Cenuuji").Should().OnlyContain(s => s.Tier == SeverityTier.Clean);
        result.Sentences.Should().ContainSingle().Which.Score.Should().Be(cenuuji.Score);
    }

    [Fact]
    public void AFusedTokenIsPenalisedToFortyAndWarns()
    {
        var result = Verifier().Verify("A certain Bubble appears.", "jistýBubble si tvé pomoci.");
        var fused = result.Spans.First(s => s.Word == "jistýBubble");

        fused.Score.Should().Be(40);
        fused.Score.Should().BeLessThan(Settings.WarningThreshold);
        fused.Tier.Should().Be(SeverityTier.Warning);
        fused.Defect.Should().Be(DefectClass.FusedToken);
    }

    [Fact]
    public void AnUntranslatedChunkIsFiveErrorWordsAtScoreTen()
    {
        var result = Verifier().Verify("Toto je test.", "this sentence stayed completely english");

        result.Spans.Should().HaveCount(5);
        result.Spans.Should().OnlyContain(s => s.Defect == DefectClass.UntranslatedChunk);
        result.Spans.Should().OnlyContain(s => s.Tier == SeverityTier.Error && s.Score == 10);
        result.UnanalyzableTokenRatePer1000.Should().BeApproximately(1000.0, 0.001);
    }

    [Fact]
    public void ACleanControlSentenceCarriesNoUnderlines()
    {
        var result = Verifier().Verify("I appreciate your help.", "Cením si tvé pomoci.");

        result.Spans.Should().OnlyContain(s => s.Tier == SeverityTier.Clean && s.Score == 100);
        result.Sentences.Should().ContainSingle().Which.Score.Should().Be(100);
        result.HasFindings.Should().BeFalse();
    }

    [Fact]
    public void ARareButRealFormTheDictionaryAcceptsScoresFull() =>
        Verifier().Verify("Vacuum test.", "Vakuum si tvé pomoci.")
            .Spans.First(s => s.Word == "Vakuum").Score.Should().Be(100);

    [Fact]
    public void AFormOnlyTheAnalyzerKnowsIsClearedByStageTwo()
    {
        var puuma = Verifier().Verify("Puma test.", "puuma si tvé pomoci.")
            .Spans.First(s => s.Word == "puuma");

        puuma.Score.Should().Be(100);
        puuma.Tier.Should().Be(SeverityTier.Clean);
        puuma.Signals.Should().Contain(h => h.SignalId == "analyzer-clear");
    }

    [Fact]
    public void AnInternalFailureIsSkippedRatherThanThrown()
    {
        var lexicon = Lexicon();

        var verifier = new TranslationVerifier(
            new ThrowingSpellChecker(),
            new FakeAnalyzer(CzechWords),
            lexicon,
            new CharNgramScorer(lexicon),
            Settings);

        var result = verifier.Verify("x", "y z");

        result.Executed.Should().BeFalse();
        result.SkipReason.Should().NotBeNull();
    }

    [Fact]
    public void TheDisabledSettingSkipsVerification()
    {
        var lexicon = Lexicon();

        var verifier = new TranslationVerifier(
            new FakeSpellChecker(CzechWords),
            new FakeAnalyzer(CzechWords),
            lexicon,
            new CharNgramScorer(lexicon),
            new VerificationSettings { Enabled = false });

        verifier.Verify("a", "b").Executed.Should().BeFalse();
    }

    [Fact]
    public void TheStageZeroGuardFlagsTemperatureRepeatPenaltyQuantisationAndKvCache()
    {
        var bad = new SamplerSettings(Temperature: 0.8, TopK: 64, TopP: 0.95, MinP: 0.05, RepeatPenalty: 1.1);

        var ids = SamplerConfigGuard
            .Validate("translategemma-4b-it-Q4_K_M", bad, kvCacheQuantized: true)
            .Select(a => a.RuleId)
            .ToList();

        ids.Should().Contain(["STAGE0-TEMP", "STAGE0-TOPK", "STAGE0-REPPEN", "STAGE0-MINP", "STAGE0-QUANT", "STAGE0-KV"]);
    }

    [Fact]
    public void AQatCheckpointPassesTheQuantisationFloor() =>
        SamplerConfigGuard
            .Validate("translategemma-4b-it-qat-Q4_0", SamplerConfigGuard.Recommended(), kvCacheQuantized: false)
            .Should().NotContain(a => a.RuleId == "STAGE0-QUANT");

    [Fact]
    public void TheRecommendedSamplerIsGreedyWithPenaltiesDisabled()
    {
        var recommended = SamplerConfigGuard.Recommended();

        recommended.Temperature.Should().Be(0);
        recommended.TopK.Should().Be(1);
        recommended.MinP.Should().Be(0);
        recommended.RepeatPenalty.Should().Be(1.0);
    }

    [Fact]
    public void TheGuardOnlySpeaksForTranslateGemma()
    {
        SamplerConfigGuard.AppliesTo("translategemma-4b-it").Should().BeTrue();
        SamplerConfigGuard.AppliesTo("EuroLLM-9B-Instruct").Should().BeFalse();

        SamplerConfigGuard
            .Validate("EuroLLM-9B-Instruct-Q6_K", new SamplerSettings(0.7, 64, 0.95, 0.05, 1.1), false)
            .Should().BeEmpty();
    }

    private sealed class FakeSpellChecker(IEnumerable<string> words) : ISpellChecker
    {
        private readonly HashSet<string> _words = new(words, StringComparer.OrdinalIgnoreCase);

        public bool IsCorrect(string word) => _words.Contains(word);
    }

    private sealed class FakeAnalyzer(IEnumerable<string> lemmatizable) : IMorphologicalAnalyzer
    {
        private readonly HashSet<string> _lemmatizable = new(lemmatizable, StringComparer.OrdinalIgnoreCase);

        public bool TryAnalyze(string word, out int lemmaCount)
        {
            lemmaCount = _lemmatizable.Contains(word) ? 1 : 0;
            return true;
        }
    }

    private sealed class ThrowingSpellChecker : ISpellChecker
    {
        public bool IsCorrect(string word) => throw new InvalidOperationException("dictionary file corrupt");
    }
}
