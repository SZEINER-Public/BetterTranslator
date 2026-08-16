using System.Windows;
using System.Windows.Controls;
using BetterTranslator.Core.Verification;

namespace BetterTranslator.App.Verification;

public static class VerificationText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(VerificationText),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty ResultProperty = DependencyProperty.RegisterAttached(
        "Result",
        typeof(VerificationResult),
        typeof(VerificationText),
        new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetResult(DependencyObject element, VerificationResult? value) =>
        element.SetValue(ResultProperty, value);

    public static VerificationResult? GetResult(DependencyObject element) =>
        (VerificationResult?)element.GetValue(ResultProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();

        var text = GetText(block);

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var result = GetResult(block);

        if (result is null)
        {
            block.Inlines.Add(new System.Windows.Documents.Run(text));
            return;
        }

        foreach (var inline in VerificationInlineRenderer.Build(text, result))
        {
            block.Inlines.Add(inline);
        }
    }
}
