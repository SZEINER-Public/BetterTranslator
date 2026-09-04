using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Engine.Verification.Signals;

namespace BetterTranslator.Engine.Verification.Coverage;

public sealed class HunspellSourceLexicon(string code, ISpellChecker checker) : ISourceLexicon
{
    public string Code { get; } = code;

    public bool Contains(string word) => word.Length > 0 && checker.IsCorrect(word);
}

public static class CoverageEvidenceFactory
{
    public static SourceLanguageEvidence Create(string? sourceLanguage, string? dataFolder)
    {
        var seeded = CoverageServices.BuildSeeded();
        var lexicons = new List<ISourceLexicon> { new WordListLexicon("en", EnglishSeedLexicon.Words) };
        var morphologies = new List<ISourceMorphology> { new SuffixMorphology("en", lexicons[0], EnglishSeedLexicon.Suffixes) };

        if (!string.IsNullOrWhiteSpace(sourceLanguage) && DictionaryStore.Find(sourceLanguage, dataFolder) is { } pair)
        {
            var code = sourceLanguage.Split('-', '_')[0];
            var checker = new HunspellSpellChecker(pair.DicPath, pair.AffPath);
            lexicons.Add(new HunspellSourceLexicon(code, checker));
        }

        return new SourceLanguageEvidence(lexicons, morphologies, seeded.Identifier);
    }
}
