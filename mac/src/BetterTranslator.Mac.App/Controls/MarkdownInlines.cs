using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Mac.App.Controls;

internal static class MarkdownInlines
{
    public static void Fill(InlineCollection? into, IEnumerable<MdInline>? inlines)
    {
        if (into is null)
        {
            return;
        }

        into.Clear();

        if (inlines is null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            into.Add(Build(inline));
        }
    }

    public static Inline Build(MdInline inline)
    {
        var run = new Run(inline.Text);

        if (inline.Bold)
        {
            run.FontWeight = FontWeight.SemiBold;
        }

        if (inline.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (inline.Code)
        {
            run.FontFamily = Get<FontFamily>("FontFamilyMono");
            run.Background = Get<IBrush>("BrushSurfaceSunken");
        }

        return inline.Link is null ? run : Anchor(run);
    }

    private static Span Anchor(Run run)
    {
        var link = new Span
        {
            Foreground = Get<IBrush>("BrushAccent"),
            TextDecorations = TextDecorations.Underline,
        };

        link.Inlines.Add(run);

        return link;
    }

    private static T Get<T>(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException($"Resource '{key}' was not found.");
}
