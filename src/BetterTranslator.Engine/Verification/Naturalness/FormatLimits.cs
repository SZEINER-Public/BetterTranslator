using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Verification.Naturalness;

public enum ReorderPermission
{
    Allowed,
    ForbiddenPositionalPlaceholders,
    ForbiddenCueBoundary,
}

public static partial class FormatLimits
{
    public const string SubtitleFormat = "subtitle";

    [GeneratedRegex(@"%[sdifuxXc]|(?<!\{)\{\}(?!\})|%[0-9]*\.?[0-9]*[sdif]")]
    private static partial Regex PositionalPlaceholder { get; }

    [GeneratedRegex(@"\{[0-9]+(?:[:,][^}]*)?\}|\{[A-Za-z_][A-Za-z0-9_]*(?:[:,][^}]*)?\}|%[0-9]+\$[sdif]|\$\{[A-Za-z_][A-Za-z0-9_]*\}")]
    private static partial Regex IndexedPlaceholder { get; }

    public static ReorderPermission Classify(string sourceSentence, string targetSentence, string format)
    {
        ArgumentNullException.ThrowIfNull(sourceSentence);
        ArgumentNullException.ThrowIfNull(targetSentence);
        ArgumentNullException.ThrowIfNull(format);

        if (string.Equals(format, SubtitleFormat, StringComparison.OrdinalIgnoreCase) && targetSentence.Contains('\n', StringComparison.Ordinal))
        {
            return ReorderPermission.ForbiddenCueBoundary;
        }

        return PositionalPlaceholder.IsMatch(sourceSentence) || PositionalPlaceholder.IsMatch(targetSentence)
            ? ReorderPermission.ForbiddenPositionalPlaceholders
            : ReorderPermission.Allowed;
    }

    public static IReadOnlyList<string> Placeholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return [.. IndexedPlaceholder.Matches(text).Select(m => m.Value).Concat(PositionalPlaceholder.Matches(text).Select(m => m.Value)).Order(StringComparer.Ordinal)];
    }

    public static bool PlaceholdersSurvive(string original, string proposed) =>
        Placeholders(original).SequenceEqual(Placeholders(proposed), StringComparer.Ordinal);
}
