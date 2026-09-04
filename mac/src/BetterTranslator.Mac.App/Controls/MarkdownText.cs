using Avalonia;
using Avalonia.Controls;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Mac.App.Controls;

public static class MarkdownText
{
    public static readonly AttachedProperty<IEnumerable<MdInline>?> InlinesProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IEnumerable<MdInline>?>("Inlines", typeof(MarkdownText));

    static MarkdownText()
    {
        InlinesProperty.Changed.AddClassHandler<TextBlock>(OnInlinesChanged);
    }

    public static void SetInlines(TextBlock target, IEnumerable<MdInline>? value) =>
        target.SetValue(InlinesProperty, value);

    public static IEnumerable<MdInline>? GetInlines(TextBlock target) =>
        target.GetValue(InlinesProperty);

    private static void OnInlinesChanged(TextBlock target, AvaloniaPropertyChangedEventArgs e) =>
        MarkdownInlines.Fill(target.Inlines, e.NewValue as IEnumerable<MdInline>);
}
