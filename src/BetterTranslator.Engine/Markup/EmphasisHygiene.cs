using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Moves whitespace out of an emphasis run.
///
/// A run whose delimiters sit against whitespace does not close: CommonMark
/// requires the closing delimiter to follow a non-whitespace character, so
/// "**Label: **" renders its asterisks as text. It is a property of the markup
/// rather than of any language, so the whitespace is dropped and nothing else in
/// the line moves.
///
/// Repaired on the assembled document rather than per unit. A unit begins after
/// the opening delimiter and ends before the closing one, so the pair is never
/// inside one answer and a per-unit pass sees a single stray asterisk it cannot
/// judge. Aligned line by line against the source, so a document that was
/// already written that way is left exactly as it was.
/// </summary>
public static class EmphasisHygiene
{
    private static readonly Regex Runs = new(
        @"(?<![*_])(?<open>\*\*|__|\*|_)(?![*_])(?<lead>[ \t]*)(?<body>[^\s](?:[^\r\n]*?[^\s])?)(?<trail>[ \t]*)(?<![*_])\k<open>(?![*_])",
        RegexOptions.Compiled);

    private static readonly Regex Fence = new(@"^\s*```", RegexOptions.Compiled);
    private static readonly Regex Indented = new(@"^(\t| {4,})", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`[^`]*`", RegexOptions.Compiled);

    public static string Restore(string translated)
    {
        ArgumentNullException.ThrowIfNull(translated);

        return Config.PipelineOptions.RepairEmphasisRuns ? RepairLine(translated) : translated;
    }

    public static string RestoreAgainst(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        if (!Config.PipelineOptions.RepairEmphasisRuns)
        {
            return translated;
        }

        var sourceLines = source.Replace("\r\n", "\n").Split('\n');
        var targetLines = translated.Replace("\r\n", "\n").Split('\n');

        if (sourceLines.Length != targetLines.Length)
        {
            return translated;
        }

        var inFence = false;
        var repaired = false;

        for (var index = 0; index < targetLines.Length; index++)
        {
            if (Fence.IsMatch(sourceLines[index]))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || Indented.IsMatch(sourceLines[index]))
            {
                continue;
            }

            if (!Damaged(targetLines[index]) || Damaged(sourceLines[index]))
            {
                continue;
            }

            targetLines[index] = RepairLine(targetLines[index]);
            repaired = true;
        }

        return repaired ? string.Join('\n', targetLines) : translated;
    }

    public static bool Damaged(string? text)
    {
        if (text is null)
        {
            return false;
        }

        return Outside(text).Any(part => Runs.Matches(part).Any(match =>
            match.Groups["lead"].Length > 0 || match.Groups["trail"].Length > 0));
    }

    private static string RepairLine(string line)
    {
        var rebuilt = new System.Text.StringBuilder();
        var cursor = 0;

        foreach (var code in InlineCode.Matches(line).Cast<Match>())
        {
            rebuilt.Append(Repair(line[cursor..code.Index]));
            rebuilt.Append(code.Value);
            cursor = code.Index + code.Length;
        }

        rebuilt.Append(Repair(line[cursor..]));

        return rebuilt.ToString();
    }

    private static string Repair(string part) =>
        Runs.Replace(
            part,
            match => match.Groups["open"].Value + match.Groups["body"].Value + match.Groups["open"].Value);

    private static IEnumerable<string> Outside(string line)
    {
        var cursor = 0;

        foreach (var code in InlineCode.Matches(line).Cast<Match>())
        {
            yield return line[cursor..code.Index];
            cursor = code.Index + code.Length;
        }

        yield return line[cursor..];
    }
}
