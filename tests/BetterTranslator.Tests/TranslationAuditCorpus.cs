using System.IO;
using Xunit;

namespace BetterTranslator.Tests;

internal static class TranslationAuditCorpus
{
    public const string EnglishFile = "claude-en.md";

    public const string ObservedFile = "claude-cs-observed.md";

    public static string PathTo(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "TranslationAudit", name);

    public static bool Present =>
        File.Exists(PathTo(EnglishFile)) && File.Exists(PathTo(ObservedFile));
}

public sealed class TranslationAuditFactAttribute : FactAttribute
{
    public TranslationAuditFactAttribute()
    {
        if (!TranslationAuditCorpus.Present)
        {
            Skip =
                $"The translation audit corpus is not in this checkout. Put {TranslationAuditCorpus.EnglishFile} "
                + $"and {TranslationAuditCorpus.ObservedFile} under tests/BetterTranslator.Tests/Fixtures/TranslationAudit to run these.";
        }
    }
}
