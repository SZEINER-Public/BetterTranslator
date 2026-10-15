using BetterTranslator.Engine.Slop;
using BetterTranslator.Engine.Terminology;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Runtime.Agents.Mcp;

namespace BetterTranslator.Runtime.Speech;

public sealed record VocabularySources(
    bool DoNotTranslate = true,
    bool DomainTerms = true,
    bool AgentTools = true,
    bool IndexedMemory = true);

public sealed record VocabularyTerm(string Term, float Weight, string Source);

public static class TechnicalVocabulary
{
    public const int DefaultMaxTerms = 8192;

    public const int DefaultHarvestBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<VocabularyTerm> ShippedTerms(VocabularySources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var terms = new List<VocabularyTerm>();

        if (sources.DoNotTranslate)
        {
            var lists = DoNotTranslate.Load();
            terms.AddRange(lists.Strict.Select(t => new VocabularyTerm(t, 3f, "do-not-translate")));
            terms.AddRange(lists.Declinable.Select(t => new VocabularyTerm(t, 2f, "do-not-translate")));
        }

        if (sources.DomainTerms)
        {
            terms.AddRange(DomainTerms.For("cs").Terms
                .Where(t => t.KeepInSource || t.IsName)
                .Select(t => new VocabularyTerm(t.Source, 2f, "domain-terms")));
        }

        if (sources.AgentTools)
        {
            terms.AddRange(BetterTranslatorTools.Names.Select(n => new VocabularyTerm(n, 1.5f, "agent-tools")));
        }

        return terms;
    }

    public static async Task<IReadOnlyList<string>> IndexedTextsAsync(
        IndexStore store,
        int maxBytes = DefaultHarvestBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var texts = new List<string>();
        var budget = maxBytes;

        foreach (var source in await store.GetSourcesAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var chunk in await store.GetChunksAsync(source.Id, cancellationToken).ConfigureAwait(false))
            {
                if (budget <= 0)
                {
                    return texts;
                }

                texts.Add(chunk.Text);
                budget -= chunk.Text.Length;
            }
        }

        return texts;
    }

    public static SpeechLexicon Build(
        VocabularySources sources,
        IEnumerable<string>? extraTerms = null,
        IEnumerable<string>? harvestTexts = null,
        int maxTerms = DefaultMaxTerms)
    {
        var lexicon = SpeechLexicon.Create(maxTerms);

        try
        {
            foreach (var term in ShippedTerms(sources))
            {
                lexicon.Add(term.Term, term.Weight);
            }

            foreach (var term in extraTerms ?? [])
            {
                lexicon.Add(term, 2f);
            }

            if (sources.IndexedMemory)
            {
                foreach (var text in harvestTexts ?? [])
                {
                    lexicon.Harvest(text);
                }
            }

            lexicon.Freeze();
            return lexicon;
        }
        catch
        {
            lexicon.Dispose();
            throw;
        }
    }
}
