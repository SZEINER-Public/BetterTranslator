using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks.Terminology;

namespace BetterTranslator.Engine.Verification.Terminology;

public static class TerminologyResolver
{
    public static TerminologyServices Resolve(VerificationSettings settings, string? targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var language = string.IsNullOrWhiteSpace(targetLanguage) ? string.Empty : targetLanguage.Split('-', '_')[0].ToLowerInvariant();
        var lemmatizer = MajkaLemmatizer.Resolve(language, settings.MajkaExePath, settings.MajkaDictPath);
        var glossary = language.Length == 0 ? TerminologyGlossary.Empty : TerminologyGlossaryAdapter.ForLanguage(language);

        return new TerminologyServices(lemmatizer, glossary);
    }
}
