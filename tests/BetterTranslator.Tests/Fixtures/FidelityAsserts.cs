using System.Text.RegularExpressions;
using BetterTranslator.Engine.Markup;
using FluentAssertions;

namespace BetterTranslator.Tests;

/// <summary>
/// What a good translation of <see cref="SpecCorpus"/> looks like, expressed as
/// properties rather than as expected text.
///
/// A fixture holding one expected Czech string pins one model's wording and
/// fails on the next model, which teaches the suite to be ignored. These
/// assertions hold for every correct translation and for no incorrect one.
/// </summary>
public static class FidelityAsserts
{
    private static readonly Regex Sentinel = new(@"\[\[\d+\]\]", RegexOptions.Compiled);

    /// <summary>
    /// No clause of the source survived. This is the reported defect's assertion:
    /// a whole sentence coming back in English is a run of its own words, in
    /// order, in the output.
    /// </summary>
    public static void NoUntranslatedProse(string source, string translated, int minRun = 4)
    {
        var residue = SourceResidue.Find(source, translated, minRun);

        residue.Should().BeEmpty(
            "every run of {0} or more source words must have been translated, and these were not: {1}",
            minRun,
            string.Join(" | ", residue.Select(r => r.Text)));
    }

    /// <summary>
    /// Names, flags, placeholders and paths appear in the output at least as
    /// often as in the source. Fewer means one was translated away.
    /// </summary>
    public static void SurvivesVerbatim(string source, string translated, IReadOnlyList<string> terms)
    {
        var lost = terms
            .Select(t => (Term: t, Source: Count(source, t), Output: Count(translated, t)))
            .Where(x => x.Source > 0 && x.Output < x.Source)
            .Select(x => $"{x.Term} ({x.Source} -> {x.Output})")
            .ToList();

        lost.Should().BeEmpty("these must survive translation unchanged: {0}", string.Join(", ", lost));
    }

    public static void NoSentinelResidue(string translated) =>
        Sentinel.Matches(translated).Should().BeEmpty("a markup sentinel reached the reader");

    public static void NotEchoed(string source, string translated) =>
        translated.Trim().Should().NotBe(source.Trim(), "the source was emitted as its own result");

    /// <summary>Every class of defect this corpus exists to catch, in one call.</summary>
    public static void NoDefects(string source, string translated, IReadOnlyList<string> terms)
    {
        NotEchoed(source, translated);
        NoSentinelResidue(translated);
        SurvivesVerbatim(source, translated, terms);
        NoUntranslatedProse(source, translated);
    }

    private static int Count(string text, string term)
    {
        var n = 0;
        var at = 0;

        while (at <= text.Length - term.Length)
        {
            var hit = text.IndexOf(term, at, StringComparison.Ordinal);

            if (hit < 0)
            {
                break;
            }

            n++;
            at = hit + term.Length;
        }

        return n;
    }
}
