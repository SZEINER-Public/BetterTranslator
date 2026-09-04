using BetterTranslator.Core.Verification.Checks.Terminology;
using BetterTranslator.Engine.Slop;
using BetterTranslator.Engine.Terminology;

namespace BetterTranslator.Engine.Verification.Terminology;

public static class TerminologyGlossaryAdapter
{
    public static TerminologyGlossary From(DomainTermTable? domain, GlossaryTables? project)
    {
        var glossary = new TerminologyGlossary();

        if (domain is not null)
        {
            foreach (var term in domain.Terms)
            {
                glossary.Add(new TerminologyEntry(term.Source, term.Accepted, term.Wrong));
            }
        }

        if (project is not null)
        {
            foreach (var keep in project.Keep)
            {
                glossary.Replace(new TerminologyEntry(keep, string.Empty, []));
            }

            foreach (var term in project.Terms)
            {
                glossary.Replace(new TerminologyEntry(term.Source, term.Target, []));
            }
        }

        return glossary;
    }

    public static TerminologyGlossary ForLanguage(string language = "cs") =>
        From(DomainTerms.For(language), Glossary.Active(language));
}
