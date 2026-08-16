namespace BetterTranslator.Engine.Markup;

/// <summary>What the adjudicator decided about one ambiguous defect.</summary>
public enum AdjudicatedVerdict
{
    /// <summary>Change nothing. The safe answer, and the default.</summary>
    Keep,

    /// <summary>Ordinary prose that should have been translated.</summary>
    Translate,

    /// <summary>Two words run together; only a space is missing.</summary>
    Space,
}

/// <summary>
/// Asking the model about a defect no rule can settle. Ported from
/// `Invoke-DefectAdjudication` in `translate-final.ps1`.
///
/// <see cref="TranslationDefects"/> sorts what it finds into certain-bad,
/// certain-ok and <see cref="DefectVerdict.Ambiguous"/>. The last group is not a
/// gap in the detector -- it is the set where no rule available locally can
/// separate a product name that correctly stayed in English from a phrase the
/// model simply failed to translate. Only something that knows both languages can.
///
/// The transport is the caller's; this class owns the prompt, the parse and the
/// cache, which is where the discipline lives.
/// </summary>
public sealed class DefectAdjudication
{
    private readonly Dictionary<string, AdjudicatedVerdict> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// One word out, no explanation. A defect list on a real document repeats the
    /// same fragment many times, so the cache is what keeps this from being one
    /// request per occurrence.
    /// </summary>
    public static string Instruction(string sourceLanguage, string targetLanguage) =>
        $"You are checking a {sourceLanguage}-to-{targetLanguage} translation. "
        + "Answer with exactly one word: TRANSLATE, KEEP or SPACE. "
        + "TRANSLATE if the fragment is ordinary prose that should have been translated. "
        + $"KEEP if it is a proper name, a product, a technical term or a unit that correctly stays in {sourceLanguage}. "
        + "SPACE if two words have been run together and only a space is missing. "
        + "No explanation.";

    /// <summary>
    /// Both lines go with the fragment. Asked about a fragment alone the model
    /// has no way to tell a product name from an untranslated clause -- the
    /// question is only answerable in context.
    /// </summary>
    public static string Question(
        string sourceLanguage,
        string targetLanguage,
        string source,
        string translated,
        string fragment) =>
        $"Original {sourceLanguage} line: {source}\n"
        + $"Translated {targetLanguage} line: {translated}\n"
        + $"Fragment in question: {fragment}\n"
        + "Answer:";

    /// <summary>
    /// Reads one answer.
    ///
    /// A verdict has to be valid FOR ITS KIND, not merely spelled correctly.
    /// Asked about the residue "joins authoritative sessions" the model answered
    /// SPACE -- a spacing verdict for a phrase with no spacing fault, which would
    /// have licensed an edit that makes no sense. Only glue can be answered SPACE;
    /// only residue can be answered TRANSLATE. Anything else falls to Keep, which
    /// changes nothing.
    /// </summary>
    public static AdjudicatedVerdict Parse(string? answer, DefectKind kind)
    {
        var word = new string((answer ?? string.Empty).Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant();

        return kind switch
        {
            DefectKind.Glue => word == "SPACE" ? AdjudicatedVerdict.Space : AdjudicatedVerdict.Keep,
            _ => word == "TRANSLATE" ? AdjudicatedVerdict.Translate : AdjudicatedVerdict.Keep,
        };
    }

    /// <summary>
    /// Adjudicates one defect, asking <paramref name="ask"/> only when this
    /// fragment has not been seen for this kind before.
    /// </summary>
    /// <param name="ask">
    /// Sends the two strings and returns the model's answer, or null. Null means
    /// Keep: a failed request must never license an edit.
    /// </param>
    public async Task<AdjudicatedVerdict> AdjudicateAsync(
        TranslationDefect defect,
        string source,
        string translated,
        string sourceLanguage,
        string targetLanguage,
        Func<string, string, CancellationToken, Task<string?>> ask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defect);
        ArgumentNullException.ThrowIfNull(ask);

        var key = defect.Kind + "\n" + defect.Text;

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var verdict = AdjudicatedVerdict.Keep;

        try
        {
            var answer = await ask(
                Instruction(sourceLanguage, targetLanguage),
                Question(sourceLanguage, targetLanguage, source, translated, defect.Text),
                cancellationToken).ConfigureAwait(false);

            verdict = Parse(answer, defect.Kind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A model that fails to answer must not be read as agreeing to a
            // change. Keep changes nothing, so it is the only safe failure.
        }

        _cache[key] = verdict;
        return verdict;
    }

    /// <summary>How many distinct questions have been asked. For the log.</summary>
    public int Asked => _cache.Count;
}
