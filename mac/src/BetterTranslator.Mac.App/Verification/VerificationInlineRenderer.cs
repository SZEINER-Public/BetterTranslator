using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using BetterTranslator.Core.Verification;

namespace BetterTranslator.Mac.App.Verification;

public static class VerificationInlineRenderer
{
    public static IEnumerable<Inline> Build(string targetText, VerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(targetText);
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Executed || result.Spans.Count == 0)
        {
            yield return new Run(targetText);
            yield break;
        }

        var pos = 0;

        foreach (var span in result.Spans.OrderBy(s => s.Start))
        {
            if (span.Start < pos || span.Start + span.Length > targetText.Length)
            {
                continue;
            }

            if (span.Start > pos)
            {
                yield return new Run(targetText[pos..span.Start]);
            }

            var run = new Run(targetText.Substring(span.Start, span.Length));

            if (!span.Exempt && span.Tier != SeverityTier.Clean)
            {
                Decorate(run, span);
            }

            yield return run;

            pos = span.Start + span.Length;
        }

        if (pos < targetText.Length)
        {
            yield return new Run(targetText[pos..]);
        }
    }

    private static void Decorate(Run run, VerificationSpan span)
    {
        var error = span.Tier == SeverityTier.Error;

        var brush = Resource<IBrush>(
            error ? "BtVerifyErrorUnderlineBrush" : "BtVerifyWarningUnderlineBrush",
            Brushes.Gray);

        var thickness = Resource(
            error ? "BtVerifyErrorUnderlineThickness" : "BtVerifyWarningUnderlineThickness",
            1.5);

        var dash = Resource(
            error ? "BtVerifyErrorDashStyle" : "BtVerifyWarningDashStyle",
            DashStyle.Dash);

        var decoration = new TextDecoration
        {
            Location = TextDecorationLocation.Underline,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeThicknessUnit = TextDecorationUnit.Pixel,
        };

        if (dash.Dashes is { Count: > 0 } dashes)
        {
            decoration.StrokeDashArray = new AvaloniaList<double>(dashes);
            decoration.StrokeDashOffset = dash.Offset;
        }

        run.TextDecorations = new TextDecorationCollection { decoration };
    }

    private static T Resource<T>(string key, T fallback) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed
            ? typed
            : fallback;
}
