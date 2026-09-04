using BetterTranslator.Core.Verification;
using BetterTranslator.Engine.Verification.Signals;

namespace BetterTranslator.Engine.Verification;

public static class VerificationFactory
{
    /// <param name="languageCode">
    /// The language the answers will be in, used to find a dictionary nobody
    /// configured. Null asks for the configured pair only, which is what the
    /// tests want and what a caller with no target language can honestly say.
    /// </param>
    /// <param name="dataFolder">The reader's dictionary folder, searched first.</param>
    public static TranslationVerifier? Create(
        VerificationSettings settings,
        string? languageCode = null,
        string? dataFolder = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return null;
        }

        var dicPath = settings.HunspellDicPath;
        var affPath = settings.HunspellAffPath;

        // A configured pair is a deliberate choice and outranks the search. A
        // half-configured one is not: it used to switch the verifier off, where
        // now it falls through to whatever is actually installed.
        if (!Readable(dicPath) || !Readable(affPath))
        {
            var found = DictionaryStore.Find(languageCode, dataFolder);

            if (found is null)
            {
                return null;
            }

            dicPath = found.DicPath;
            affPath = found.AffPath;
        }

        try
        {
            ISpellChecker spell = new HunspellSpellChecker(dicPath!, affPath!);

            IMorphologicalAnalyzer analyzer = Readable(settings.MajkaExePath) && Readable(settings.MajkaDictPath)
                ? new MajkaCliAnalyzer(settings.MajkaExePath!, settings.MajkaDictPath!)
                : NullMorphologicalAnalyzer.Instance;

            var lexicon = Readable(settings.FrequencyListPath)
                ? FrequencyLexicon.LoadFile(settings.FrequencyListPath!)
                : null;

            var ngram = lexicon is null ? null : new CharNgramScorer(lexicon);

            return new TranslationVerifier(spell, analyzer, lexicon, ngram, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public const string DefaultSourceLanguage = "en";

    public static VerificationPipeline CreatePipeline(
        VerificationSettings settings,
        string? languageCode = null,
        string? dataFolder = null,
        string? sourceLanguageCode = DefaultSourceLanguage)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Core.Verification.Checks.Coverage.CoverageServices.Configure(
            Coverage.CoverageEvidenceFactory.Create(sourceLanguageCode, dataFolder));

        return new VerificationPipeline(Create(settings, languageCode, dataFolder), settings);
    }

    private static bool Readable(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
