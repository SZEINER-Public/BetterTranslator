using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using BetterTranslator.Core.Verification;

namespace BetterTranslator.Mac.App.Verification;

public static class VerificationText
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(VerificationText));

    public static readonly AttachedProperty<VerificationResult?> ResultProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, VerificationResult?>("Result", typeof(VerificationText));

    static VerificationText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>(OnChanged);
        ResultProperty.Changed.AddClassHandler<TextBlock>(OnChanged);
    }

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    public static void SetResult(TextBlock element, VerificationResult? value) =>
        element.SetValue(ResultProperty, value);

    public static VerificationResult? GetResult(TextBlock element) =>
        element.GetValue(ResultProperty);

    private static void OnChanged(TextBlock block, AvaloniaPropertyChangedEventArgs e)
    {
        if (block.Inlines is not { } inlines)
        {
            return;
        }

        inlines.Clear();

        var text = GetText(block);

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var result = GetResult(block);

        if (result is null)
        {
            inlines.Add(new Run(text));
            return;
        }

        foreach (var inline in VerificationInlineRenderer.Build(text, result))
        {
            inlines.Add(inline);
        }
    }
}
