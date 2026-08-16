using System.Windows;
using System.Windows.Controls;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.App.Controls;

/// <summary>
/// Fills a TextBlock from a run of Markdown inlines. WPF cannot bind an Inlines
/// collection, so the marks are applied here rather than in markup; every value
/// still comes from a token.
/// </summary>
public static class MarkdownText
{
    public static readonly DependencyProperty InlinesProperty = DependencyProperty.RegisterAttached(
        "Inlines",
        typeof(IEnumerable<MdInline>),
        typeof(MarkdownText),
        new PropertyMetadata(null, OnInlinesChanged));

    public static void SetInlines(DependencyObject target, IEnumerable<MdInline>? value) =>
        target.SetValue(InlinesProperty, value);

    public static IEnumerable<MdInline>? GetInlines(DependencyObject target) =>
        (IEnumerable<MdInline>?)target.GetValue(InlinesProperty);

    private static void OnInlinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text)
        {
            MarkdownInlines.Fill(text.Inlines, e.NewValue as IEnumerable<MdInline>);
        }
    }
}
