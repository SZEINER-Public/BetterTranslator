namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Whether the document still has the lines it started with.
///
/// Every count the ported structural comparison makes can hold while whole
/// paragraphs go missing: fences, table rows and headings were all equal on a
/// document that came back thirty nine lines shorter. The Markdown path replaces
/// byte ranges and no answer may introduce or swallow a line break, so an
/// unequal count is content dropped or welded together.
///
/// Separate from <see cref="DocumentStructure"/> deliberately. That one mirrors a
/// reference implementation answer for answer, and its verdicts are pinned by
/// recorded fixtures, so a new check belongs beside it rather than inside it.
/// </summary>
public static class LineParity
{
    public static string? Compare(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        var before = Count(source);
        var after = Count(translated);

        return before == after ? null : $"lines: {before} in source, {after} in output";
    }

    public static int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Replace("\r\n", "\n").Split('\n').Length;
    }
}
