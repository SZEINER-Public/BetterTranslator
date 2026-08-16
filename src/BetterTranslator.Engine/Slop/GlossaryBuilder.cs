using System.Text;

namespace BetterTranslator.Engine.Slop;

/// <summary>What a build found, and the Markdown it proposes.</summary>
public sealed record GlossaryProposal(
    IReadOnlyList<DocumentTerm> Vocabulary,
    IReadOnlyList<TermInconsistency> Inconsistent,
    string Markdown)
{
    public bool HasAnything => Vocabulary.Count > 0;

    /// <summary>Exit code 28 in the reference: a run with nothing to analyse.</summary>
    public const int NothingToAnalyse = 28;
}

/// <summary>
/// Derives a document's technical vocabulary, measures how the translation
/// actually treats each term, and proposes a glossary for review. Ported from
/// `glossary-build.ps1`.
///
/// The header of that script records what does NOT work, and it is worth keeping:
/// asking the local model to classify each word was built and measured first, and
/// EuroLLM answered KEEP for ten words out of ten, including "the". Batching was
/// no better. It is a translation model, not a classifier, and a glossary built
/// from those answers is worse than none.
///
/// What works needs no classifier at all, because of two observations. The
/// infinite set is never needed -- only the words THIS document uses matter, and
/// the author declared them by putting words in backticks. And the defect that
/// actually shows up is INCONSISTENCY, not wrongness: you do not need to know the
/// correct rendering of "provision" to see that the document used eight of them.
///
/// So this decides nothing. It measures, and proposes the majority. A person
/// settles the handful in one sitting and the glossary enforces it forever.
/// </summary>
public static class GlossaryBuilder
{
    /// <summary>
    /// Builds a proposal. <paramref name="translated"/> may be null, in which
    /// case the vocabulary is derived and nothing is measured.
    ///
    /// A translation with a different line count is not comparable and is
    /// reported as such rather than compared anyway: the measurement pairs lines
    /// by index, and a shifted pairing would report every term as inconsistent.
    /// </summary>
    public static GlossaryProposal Build(
        string sourceText,
        string? translated = null,
        string documentName = "the document",
        string language = "Czech",
        int minFrequency = 2,
        int minOccurrences = 3)
    {
        ArgumentNullException.ThrowIfNull(sourceText);

        var vocabulary = DocumentTerms.Find(sourceText, minFrequency);
        IReadOnlyList<TermInconsistency> inconsistent = [];

        if (translated is not null)
        {
            var sourceLines = sourceText.ReplaceLineEndings("\n").Split('\n');
            var targetLines = translated.ReplaceLineEndings("\n").Split('\n');

            if (sourceLines.Length == targetLines.Length)
            {
                inconsistent = DocumentTerms.Inconsistencies(
                    sourceLines, targetLines, vocabulary, minOccurrences);
            }
        }

        return new GlossaryProposal(vocabulary, inconsistent, Render(vocabulary, inconsistent, documentName, language));
    }

    private static string Render(
        IReadOnlyList<DocumentTerm> vocabulary,
        IReadOnlyList<TermInconsistency> inconsistent,
        string documentName,
        string language)
    {
        var md = new StringBuilder();

        md.AppendLine($"# {language} glossary - proposed").AppendLine();
        md.AppendLine($"Generated from `{documentName}`.");
        md.AppendLine("Nothing here was written by hand and no model was asked. The vocabulary is");
        md.AppendLine("derived from the document's own inline code spans; the split below is measured");
        md.AppendLine("against the existing translation.").AppendLine();
        md.AppendLine("**You settle these, once.** A term rendered one way in some lines and another");
        md.AppendLine("way elsewhere is the defect worth fixing, and no script can know which way is");
        md.AppendLine("right. Move a row to the table that matches your decision and delete the rest of");
        md.AppendLine("this file.").AppendLine();

        if (inconsistent.Count > 0)
        {
            md.AppendLine("## Decide these").AppendLine();
            md.AppendLine("| Term | Kept in English | Translated | Majority | Lines that kept it |");
            md.AppendLine("|---|---|---|---|---|");

            foreach (var x in inconsistent)
            {
                var at = string.Join(", ", x.KeptAt.Take(6));

                if (x.KeptAt.Count > 6)
                {
                    at += ", ...";
                }

                md.AppendLine($"| {x.Term} | {x.Kept} | {x.Translated} | {x.Majority} | {at} |");
            }

            md.AppendLine();
            md.AppendLine("### If you choose \"keep in English\"").AppendLine();
            md.AppendLine("Add the term to the strict do-not-translate list. It is then protected as a span");
            md.AppendLine("and never reaches the model, so it cannot be translated or inflected at all.").AppendLine();
            md.AppendLine("### If you choose \"translate\"").AppendLine();
            md.AppendLine("Fill in the stem and the word and move the row here. The stem is matched against");
            md.AppendLine("the answer, so it must be the part every inflected form shares; the word is what");
            md.AppendLine("the model is shown, so it must be a real word in the right part of speech.").AppendLine();
            md.AppendLine("## Required terms").AppendLine();
            md.AppendLine($"| English | {language} stem (checked) | {language} word (shown to the model) |");
            md.AppendLine("|---|---|---|");

            foreach (var x in inconsistent.Where(x => x.Majority == "translated"))
            {
                md.AppendLine($"| {x.Term} |  |  |");
            }

            md.AppendLine();
        }

        md.AppendLine("## Full derived vocabulary").AppendLine();
        md.AppendLine("Every word this document uses both as code and as prose, minus closed-class");
        md.AppendLine("English. Listed for reference: no rule is applied from this table.").AppendLine();
        md.AppendLine("| Term | Times in prose |");
        md.AppendLine("|---|---|");

        foreach (var t in vocabulary)
        {
            md.AppendLine($"| {t.Term} | {t.Count} |");
        }

        return md.AppendLine().ToString();
    }
}
