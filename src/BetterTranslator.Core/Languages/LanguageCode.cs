using System.Globalization;

namespace BetterTranslator.Core.Languages;

public readonly record struct LanguageCode
{
    public static LanguageCode Unknown => default;

    private LanguageCode(string value, string script)
    {
        Value = value;
        Script = script;
    }

    public string Value { get; } = string.Empty;

    public string Script { get; } = string.Empty;

    public bool IsUnknown => string.IsNullOrEmpty(Value);

    public string BaseLanguage
    {
        get
        {
            if (IsUnknown)
            {
                return string.Empty;
            }

            var cut = Value.IndexOf('-');
            return cut < 0 ? Value : Value[..cut];
        }
    }

    public static LanguageCode From(string? tag, string? script = null)
    {
        var trimmed = tag?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return Unknown;
        }

        var parts = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return Unknown;
        }

        var canonical = new string[parts.Length];
        canonical[0] = parts[0].ToLowerInvariant();
        var embedded = string.Empty;

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part.Length == 4 && part.All(char.IsLetter))
            {
                canonical[i] = Title(part);
                embedded = canonical[i];
                continue;
            }

            canonical[i] = part.Length is 2 or 3 && part.All(char.IsLetterOrDigit)
                ? part.ToUpperInvariant()
                : part.ToLowerInvariant();
        }

        var resolved = embedded.Length > 0
            ? embedded
            : string.IsNullOrWhiteSpace(script) ? string.Empty : Title(script.Trim());

        return new LanguageCode(string.Join('-', canonical), resolved);
    }

    public bool SameLanguageAs(LanguageCode other)
    {
        if (IsUnknown || other.IsUnknown)
        {
            return false;
        }

        if (!string.Equals(BaseLanguage, other.BaseLanguage, StringComparison.Ordinal))
        {
            return false;
        }

        if (Script.Length == 0 || other.Script.Length == 0)
        {
            return true;
        }

        return string.Equals(Script, other.Script, StringComparison.Ordinal);
    }

    public override string ToString() => Value;

    private static string Title(string part) =>
        char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
}
