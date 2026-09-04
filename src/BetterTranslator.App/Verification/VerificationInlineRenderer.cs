using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Gate;

namespace BetterTranslator.App.Verification;

public enum InlineTier
{
    Clean,
    Suggestion,
    Warning,
    Error,
    Defect,
}

public sealed record InlineMark(int Start, int Length, InlineTier Tier, string Tooltip)
{
    public int End => Start + Length;
}

public static class VerificationInlineRenderer
{
    public static IEnumerable<Inline> Build(string targetText, VerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(targetText);
        ArgumentNullException.ThrowIfNull(result);

        var marks = Marks(targetText, result);

        if (marks.Count == 0)
        {
            yield return new Run(targetText);
            yield break;
        }

        var pos = 0;

        foreach (var mark in marks)
        {
            if (mark.Start > pos)
            {
                yield return new Run(targetText[pos..mark.Start]);
            }

            var run = new Run(targetText.Substring(mark.Start, mark.Length));

            if (mark.Tier != InlineTier.Clean)
            {
                Decorate(run, mark);
            }

            yield return run;
            pos = mark.End;
        }

        if (pos < targetText.Length)
        {
            yield return new Run(targetText[pos..]);
        }
    }

    public static IReadOnlyList<InlineMark> Marks(string targetText, VerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(targetText);
        ArgumentNullException.ThrowIfNull(result);

        var raw = new List<InlineMark>();

        if (result.Executed)
        {
            foreach (var span in result.Spans.Where(s => !s.Exempt && s.Tier != SeverityTier.Clean))
            {
                raw.Add(new InlineMark(span.Start, span.Length, span.Tier == SeverityTier.Error ? InlineTier.Error : InlineTier.Warning, SpanTooltip(span)));
            }
        }

        if (result.Gate is { } gate)
        {
            foreach (var defect in gate.Defects.Where(d => d.TargetRange.Length > 0))
            {
                raw.Add(new InlineMark(defect.TargetRange.Offset, defect.TargetRange.Length, InlineTier.Defect, string.Join(", ", defect.CheckIds) + ": " + defect.Finding.Evidence));
            }

            foreach (var suggestion in gate.Routed.Where(r => r.Action == CheckAction.Rewrite && r.TargetRange.Length > 0))
            {
                raw.Add(new InlineMark(suggestion.TargetRange.Offset, suggestion.TargetRange.Length, InlineTier.Suggestion, string.Join(", ", suggestion.CheckIds) + ": " + suggestion.Finding.Evidence));
            }
        }

        return Flatten(raw.Where(m => m.Start >= 0 && m.End <= targetText.Length && m.Length > 0).ToList());
    }

    private static IReadOnlyList<InlineMark> Flatten(IReadOnlyList<InlineMark> marks)
    {
        if (marks.Count == 0)
        {
            return [];
        }

        var boundaries = marks.SelectMany(m => new[] { m.Start, m.End }).Distinct().Order().ToList();
        var flat = new List<InlineMark>();

        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            var start = boundaries[i];
            var end = boundaries[i + 1];
            var covering = marks.Where(m => m.Start <= start && m.End >= end).ToList();

            if (covering.Count == 0)
            {
                continue;
            }

            var top = covering.OrderByDescending(m => m.Tier).ThenBy(m => m.Start).First();
            var tooltip = string.Join("\n", covering.OrderByDescending(m => m.Tier).Select(m => m.Tooltip).Distinct(StringComparer.Ordinal));
            flat.Add(new InlineMark(start, end - start, top.Tier, tooltip));
        }

        return flat;
    }

    private static string SpanTooltip(VerificationSpan span)
    {
        var signals = string.Join(
            "; ",
            span.Signals
                .Where(s => s.Penalty > 0 || s.SignalId.StartsWith("analyzer", StringComparison.Ordinal))
                .Select(s => s.Detail));

        return $"{span.Tier} {span.Score}/100"
            + (span.Defect != DefectClass.None ? $" ({span.Defect})" : string.Empty)
            + (signals.Length > 0 ? $": {signals}" : string.Empty);
    }

    private static void Decorate(Run run, InlineMark mark)
    {
        var key = mark.Tier switch
        {
            InlineTier.Defect => "Defect",
            InlineTier.Error => "Error",
            InlineTier.Suggestion => "Suggestion",
            _ => "Warning",
        };

        var brush = Resource<Brush>("BtVerify" + key + "UnderlineBrush", SystemColors.GrayTextBrush);
        var thickness = Resource("BtVerify" + key + "UnderlineThickness", 1.5);
        var dash = Resource("BtVerify" + key + "DashStyle", DashStyles.Dash);
        var pen = new Pen(brush, thickness) { DashStyle = dash };

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        run.TextDecorations =
        [
            new TextDecoration { Location = TextDecorationLocation.Underline, Pen = pen },
        ];

        run.ToolTip = new ToolTip { Content = mark.Tooltip };
        ToolTipService.SetInitialShowDelay(run, 200);
    }

    private static T Resource<T>(string key, T fallback) =>
        Application.Current?.TryFindResource(key) is T typed ? typed : fallback;
}
