using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Whole-document structural comparison, run once after all chunks are joined.
/// Ported from `Test-DocumentStructure`.
///
/// The per-chunk gate cannot see a fence that was closed in the wrong chunk, so
/// this is the backstop that would have caught the odd fence count outright --
/// 44 fences became 37, and every fence after the orphan rendered as one giant
/// code block.
/// </summary>
public static class DocumentStructure
{
    private static readonly (string Name, Regex Pattern)[] Checks =
    [
        ("code fences", new Regex("^```", RegexOptions.Multiline | RegexOptions.Compiled)),
        ("table rows", new Regex(@"^\|", RegexOptions.Multiline | RegexOptions.Compiled)),
        ("headings", new Regex("^#{1,6} ", RegexOptions.Multiline | RegexOptions.Compiled)),
    ];

    private static readonly Regex Fence = new("^```", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Everything that differs, or an empty list when the document came through
    /// intact.
    ///
    /// Empty means clean. The original had to note this: wrapping the result in
    /// an array turned an empty list into a one-element one, so a document with
    /// nothing wrong reported exactly one problem.
    /// </summary>
    public static IReadOnlyList<string> Compare(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        var issues = new List<string>();

        foreach (var (name, pattern) in Checks)
        {
            var before = pattern.Matches(source).Count;
            var after = pattern.Matches(translated).Count;

            if (before != after)
            {
                issues.Add($"{name}: {before} in source, {after} in output");
            }
        }

        var fences = Fence.Matches(translated).Count;

        if (fences % 2 != 0)
        {
            issues.Add($"code fences are unbalanced ({fences}) - the document will render as one code block");
        }

        return issues;
    }
}
