using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification;
using BetterTranslator.Engine.Verification.Signals;

namespace BetterTranslator.Engine.Verification;

public sealed class TranslationVerifier
{
    private readonly ISpellChecker _spell;
    private readonly IMorphologicalAnalyzer _analyzer;
    private readonly FrequencyLexicon? _lexicon;
    private readonly CharNgramScorer? _ngram;
    private readonly VerificationSettings _s;

    public TranslationVerifier(
        ISpellChecker spellChecker,
        IMorphologicalAnalyzer? analyzer,
        FrequencyLexicon? lexicon,
        CharNgramScorer? ngramScorer,
        VerificationSettings settings)
    {
        _spell = spellChecker;
        _analyzer = analyzer ?? NullMorphologicalAnalyzer.Instance;
        _lexicon = lexicon;
        _ngram = ngramScorer;
        _s = settings;
    }

    public VerificationResult Verify(string sourceText, string targetText) => Verify(sourceText, targetText, []);

    public VerificationResult Verify(string sourceText, string targetText, Core.Verification.Gate.GateRunResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return Verify(sourceText, targetText, gate.Findings);
    }

    public VerificationResult Verify(string sourceText, string targetText, IReadOnlyList<CheckFinding> checkFindings)
    {
        ArgumentNullException.ThrowIfNull(checkFindings);

        if (!_s.Enabled)
        {
            return VerificationResult.Skipped("disabled by setting");
        }

        try
        {
            return VerifyCore(sourceText, targetText, checkFindings);
        }
        catch (Exception ex)
        {
            return VerificationResult.Skipped($"verification error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private VerificationResult VerifyCore(string sourceText, string targetText, IReadOnlyList<CheckFinding> checkFindings)
    {
        Core.Verification.Checks.CheckInstrumentation.Hit("verifier/verify-core");
        Core.Verification.Checks.CheckInstrumentation.Hit(checkFindings.Count == 0 ? "verifier/findings-empty" : "verifier/findings-present");
        var exemption = new SourceSpanExemption(sourceText);
        var spans = new List<VerificationSpan>();

        foreach (var (word, start) in WordTokenizer.Tokenize(targetText))
        {
            var span = new VerificationSpan { Start = start, Length = word.Length, Word = word };
            spans.Add(span);

            if (exemption.IsExempt(word))
            {
                span.Exempt = true;
                continue;
            }

            var violation = CharLegalityCheck.Check(word);
            var ngramFail = false;
            double avgLp = 0;

            if (_ngram is not null)
            {
                avgLp = _ngram.AverageLogProb(word);
                ngramFail = avgLp < _s.NgramLogProbFloor;
            }

            var failA = violation is not null || ngramFail;
            var hunspellFail = !_spell.IsCorrect(word);

            if (FusedTokenCheck.HasCamelBoundary(word))
            {
                span.Defect = DefectClass.FusedToken;
                Apply(span, new SignalHit(
                    "fused-boundary",
                    "F34",
                    _s.PenaltyFusedBoundary,
                    "lowercase-to-uppercase boundary inside token"));
            }

            var analyzerKnown = false;
            var lemmas = 0;

            if (hunspellFail)
            {
                analyzerKnown = _analyzer.TryAnalyze(word, out lemmas);
                span.Unanalyzable = !analyzerKnown || lemmas == 0;
            }

            if (failA && hunspellFail)
            {
                if (violation is not null)
                {
                    Apply(span, new SignalHit("char-legality", "F35,F18", _s.PenaltyIllegalCluster, violation));
                }

                if (ngramFail)
                {
                    Apply(span, new SignalHit(
                        "char-ngram",
                        "F31",
                        _s.PenaltyNgramImplausible,
                        $"avg bigram logP {avgLp:F2} < floor {_s.NgramLogProbFloor:F2}"));
                }

                Apply(span, new SignalHit(
                    "hunspell",
                    "F27,F28,F29",
                    _s.PenaltyHunspellReject,
                    "rejected by cs_CZ dictionary"));

                if (analyzerKnown && lemmas > 0)
                {
                    span.Score = 100;
                    span.Signals.Add(new SignalHit(
                        "analyzer-clear",
                        "F23,F25,F26",
                        0,
                        $"morphological analyzer found {lemmas} lemma(s); Stage 1 flag cleared"));
                    span.Defect = DefectClass.None;
                    span.Unanalyzable = false;
                }
                else if (analyzerKnown && lemmas == 0
                    && (_lexicon is null || _lexicon.BelowFloor(word, _s.MinFrequency)))
                {
                    span.Score = Math.Min(span.Score, _s.ConfirmedMalformedScore);
                    span.Defect = span.Defect == DefectClass.FusedToken
                        ? DefectClass.FusedToken
                        : DefectClass.MalformedForm;
                    span.Signals.Add(new SignalHit(
                        "analyzer-confirm",
                        "F23,F25,F31",
                        0,
                        "zero lemmas and below frequency floor: confirmed defect"));
                }
                else if (!analyzerKnown)
                {
                    span.Signals.Add(new SignalHit(
                        "analyzer-unavailable",
                        "F23,F25",
                        0,
                        "morphological analyzer not configured; Stage 2 skipped"));
                }
            }
            else if (hunspellFail || violation is not null || ngramFail)
            {
                span.Signals.Add(new SignalHit(
                    "stage1-partial",
                    "F27",
                    0,
                    violation ?? (ngramFail ? "ngram below floor" : "hunspell reject only")));
            }
        }

        ClassifyUntranslatedChunks(spans);
        RatioScoring.Apply(spans, checkFindings);
        RuntimeScoring.Apply(spans, checkFindings, _s.RuntimeSignalWeight);

        foreach (var span in spans)
        {
            span.Tier = TierOf(span.Score);
        }

        var sentences = ScoreSentences(targetText, spans);

        var rate = spans.Count == 0
            ? 0
            : 1000.0 * spans.Count(x => x.Unanalyzable && !x.Exempt) / spans.Count;

        return new VerificationResult
        {
            Executed = true,
            Spans = spans,
            Sentences = sentences,
            UnanalyzableTokenRatePer1000 = rate,
        };
    }

    private void ClassifyUntranslatedChunks(List<VerificationSpan> spans)
    {
        var runStart = -1;

        for (var i = 0; i <= spans.Count; i++)
        {
            var inRun = i < spans.Count && spans[i].Unanalyzable && !spans[i].Exempt;

            if (inRun && runStart < 0)
            {
                runStart = i;
            }

            if (inRun || runStart < 0)
            {
                continue;
            }

            if (i - runStart >= _s.UntranslatedChunkMinRun)
            {
                for (var j = runStart; j < i; j++)
                {
                    var s = spans[j];
                    s.Defect = DefectClass.UntranslatedChunk;
                    s.Score = Math.Min(s.Score, _s.UntranslatedChunkScore);
                    s.Signals.Add(new SignalHit(
                        "untranslated-chunk",
                        "F27,F23",
                        0,
                        $"run of {i - runStart} consecutive unanalyzable words"));
                }
            }

            runStart = -1;
        }
    }

    private SeverityTier TierOf(int score) =>
        score < _s.ErrorThreshold ? SeverityTier.Error
        : score < _s.WarningThreshold ? SeverityTier.Warning
        : SeverityTier.Clean;

    private IReadOnlyList<SentenceScore> ScoreSentences(string targetText, List<VerificationSpan> spans)
    {
        var result = new List<SentenceScore>();
        var sentStart = 0;

        for (var i = 0; i <= targetText.Length; i++)
        {
            var end = i == targetText.Length || targetText[i] is '.' or '!' or '?' or '\n';

            if (!end)
            {
                continue;
            }

            var sentEnd = Math.Min(i + 1, targetText.Length);

            if (sentEnd > sentStart)
            {
                var inSentence = spans
                    .Where(s => !s.Exempt && s.Start >= sentStart && s.Start < sentEnd)
                    .ToList();

                if (inSentence.Count > 0)
                {
                    var score = inSentence.Min(s => s.Score);
                    result.Add(new SentenceScore(sentStart, sentEnd - sentStart, score, TierOf(score)));
                }
            }

            sentStart = sentEnd;
        }

        return result;
    }

    private static void Apply(VerificationSpan span, SignalHit hit)
    {
        span.Signals.Add(hit);
        span.Score = Math.Max(0, span.Score - hit.Penalty);
    }
}
