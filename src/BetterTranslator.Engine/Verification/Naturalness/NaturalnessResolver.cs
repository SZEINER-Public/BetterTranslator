using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks.Naturalness;

namespace BetterTranslator.Engine.Verification.Naturalness;

public static class NaturalnessResolver
{
    public static NaturalnessServices Resolve(VerificationSettings settings, string? targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var language = string.IsNullOrWhiteSpace(targetLanguage) ? string.Empty : targetLanguage.Split('-', '_')[0].ToLowerInvariant();
        var tagger = MajkaTagger.Resolve(language, settings.MajkaExePath, settings.MajkaDictPath);

        return NaturalnessServices.Shipped(tagger);
    }
}
