using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BetterTranslator.Core.Verification;

namespace BetterTranslator.App.Verification;

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

        var brush = Resource<Brush>(
            error ? "BtVerifyErrorUnderlineBrush" : "BtVerifyWarningUnderlineBrush",
            SystemColors.GrayTextBrush);

        var thickness = Resource(
            error ? "BtVerifyErrorUnderlineThickness" : "BtVerifyWarningUnderlineThickness",
            1.5);

        var dash = Resource(
            error ? "BtVerifyErrorDashStyle" : "BtVerifyWarningDashStyle",
            DashStyles.Dash);

        var pen = new Pen(brush, thickness) { DashStyle = dash };

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        run.TextDecorations =
        [
            new TextDecoration { Location = TextDecorationLocation.Underline, Pen = pen },
        ];

        var signals = string.Join(
            "; ",
            span.Signals
                .Where(s => s.Penalty > 0 || s.SignalId.StartsWith("analyzer", StringComparison.Ordinal))
                .Select(s => s.Detail));

        run.ToolTip = new ToolTip
        {
            Content = $"{span.Tier} {span.Score}/100"
                + (span.Defect != DefectClass.None ? $" ({span.Defect})" : string.Empty)
                + (signals.Length > 0 ? $": {signals}" : string.Empty),
        };

        ToolTipService.SetInitialShowDelay(run, 200);
    }

    private static T Resource<T>(string key, T fallback) =>
        Application.Current?.TryFindResource(key) is T typed ? typed : fallback;
}
