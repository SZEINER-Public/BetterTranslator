using System.Runtime.CompilerServices;

namespace BetterTranslator.Core.Verification.Checks.Terminology;

public sealed class TerminologyServices
{
    public const string NotAttachedReason = "no terminology services were attached to this run";

    public TerminologyServices(ILemmatizer lemmatizer, TerminologyGlossary? glossary = null)
    {
        ArgumentNullException.ThrowIfNull(lemmatizer);

        Lemmatizer = lemmatizer;
        Glossary = glossary ?? TerminologyGlossary.Empty;
    }

    public ILemmatizer Lemmatizer { get; }

    public TerminologyGlossary Glossary { get; }

    public static TerminologyServices Unavailable(string language, string reason) =>
        new(new UnavailableLemmatizer(language, reason));

    public string? SkipReason(CheckRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Lemmatizer.Available)
        {
            return "lemmatizer unavailable for " + Describe(settings.TargetLanguage) + ": " + Lemmatizer.UnavailableReason;
        }

        if (settings.TargetLanguage.Length > 0
            && Lemmatizer.Language.Length > 0
            && !string.Equals(Lemmatizer.Language, settings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return "lemmatizer covers " + Lemmatizer.Language + ", run targets " + settings.TargetLanguage;
        }

        return null;
    }

    private static string Describe(string language) => language.Length == 0 ? "the target language" : language;
}

public static class TerminologyPorts
{
    private static readonly ConditionalWeakTable<CheckContext, TerminologyServices> Attached = new();

    public static void Attach(CheckContext context, TerminologyServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        Attached.AddOrUpdate(context, services);
    }

    public static TerminologyServices For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Attached.TryGetValue(context, out var services)
            ? services
            : TerminologyServices.Unavailable(context.Settings.TargetLanguage, TerminologyServices.NotAttachedReason);
    }
}
