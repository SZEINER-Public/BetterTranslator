namespace BetterTranslator.Core.Verification.Checks.Terminology;

public interface ILemmatizer
{
    string Language { get; }

    bool Available { get; }

    string UnavailableReason { get; }

    string? Lemma(string word);
}

public sealed class UnavailableLemmatizer(string language, string reason) : ILemmatizer
{
    public string Language { get; } = language;

    public bool Available => false;

    public string UnavailableReason { get; } = reason;

    public string? Lemma(string word) => null;
}

public sealed class TableLemmatizer : ILemmatizer
{
    private readonly Dictionary<string, string> _forms = new(StringComparer.OrdinalIgnoreCase);

    public TableLemmatizer(string language)
    {
        Language = language;
    }

    public string Language { get; }

    public bool Available => true;

    public string UnavailableReason => string.Empty;

    public TableLemmatizer Add(string lemma, params string[] forms)
    {
        ArgumentNullException.ThrowIfNull(lemma);

        _forms[lemma] = lemma;

        foreach (var form in forms)
        {
            _forms[form] = lemma;
        }

        return this;
    }

    public string? Lemma(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        return _forms.TryGetValue(word, out var lemma) ? lemma : null;
    }
}

public static class Lemmas
{
    public static string Of(ILemmatizer lemmatizer, string word)
    {
        ArgumentNullException.ThrowIfNull(lemmatizer);
        ArgumentNullException.ThrowIfNull(word);

        var lemma = lemmatizer.Lemma(word) ?? lemmatizer.Lemma(word.ToLowerInvariant());

        return (lemma ?? word).ToLowerInvariant();
    }

    public static bool Known(ILemmatizer lemmatizer, string word)
    {
        ArgumentNullException.ThrowIfNull(lemmatizer);
        ArgumentNullException.ThrowIfNull(word);

        return lemmatizer.Lemma(word) is not null || lemmatizer.Lemma(word.ToLowerInvariant()) is not null;
    }

    public static IReadOnlyList<string> Sequence(ILemmatizer lemmatizer, string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        return [.. Words(phrase).Select(word => Of(lemmatizer, word))];
    }

    public static IReadOnlyList<string> Words(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        return phrase.Split([' ', '\t', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
