using System.Text;

namespace BetterTranslator.Mac.Parity;

public sealed record ScreenOutcome(
    string Screen,
    string Size,
    IReadOnlyList<GateResult> Gates,
    DiffOutcome Diff,
    IReadOnlyList<DeltaEntry> Deltas,
    string Folder);

public static class Report
{
    public static void Write(string path, IReadOnlyList<ScreenOutcome> outcomes, DeltaFile deltas, string hostNote)
    {
        var text = new StringBuilder();

        text.AppendLine("# Parity");
        text.AppendLine();
        text.AppendLine("Host: " + hostNote);
        text.AppendLine();
        text.AppendLine("Rendered at 1440x900 and 1280x800 logical size, light theme, fixed DPI, fixed seeded content. The wrapper renders through the Avalonia headless platform with the Skia backend, which has no windowing backend: no operating system window is created, shown or focused, and no frame is taken from the interactive desktop.");
        text.AppendLine();
        text.AppendLine("## Gates");
        text.AppendLine();
        text.AppendLine("| Screen | Size | Structural | Token | Text | String | Pixel |");
        text.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var outcome in outcomes)
        {
            text.Append("| ").Append(outcome.Screen).Append(" | ").Append(outcome.Size).Append(" | ");
            text.Append(Cell(outcome, "structural")).Append(" | ");
            text.Append(Cell(outcome, "token")).Append(" | ");
            text.Append(Cell(outcome, "text")).Append(" | ");
            text.Append(Cell(outcome, "string")).Append(" | ");
            text.Append(Cell(outcome, "pixel")).AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("## Contact sheet");
        text.AppendLine();
        text.AppendLine("| Screen | Size | windows.png | mac.png | diff.png | trees |");
        text.AppendLine("|---|---|---|---|---|---|");

        foreach (var outcome in outcomes)
        {
            var folder = outcome.Folder;

            text.Append("| ").Append(outcome.Screen).Append(" | ").Append(outcome.Size).Append(" | ");
            text.Append(Link(folder, "windows.png")).Append(" | ");
            text.Append(Link(folder, "mac.png")).Append(" | ");
            text.Append(Link(folder, "diff.png")).Append(" | ");
            text.Append(Link(folder, "mac.tree.json")).Append(' ').Append(Link(folder, "windows.tree.json")).AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("## Failures and deferrals");
        text.AppendLine();
        text.AppendLine("| Screen | Size | Gate | Verdict | Detail |");
        text.AppendLine("|---|---|---|---|---|");

        foreach (var outcome in outcomes)
        {
            foreach (var gate in outcome.Gates.Where(g => g.Verdict is GateVerdict.Fail))
            {
                text.Append("| ").Append(outcome.Screen).Append(" | ").Append(outcome.Size).Append(" | ")
                    .Append(gate.Gate).Append(" | FAIL | ").Append(Escape(gate.Detail)).AppendLine(" |");
            }
        }

        var deferred = outcomes.SelectMany(o => o.Gates)
            .Where(g => g.Verdict is GateVerdict.Deferred or GateVerdict.Delta)
            .GroupBy(g => (g.Gate, g.Verdict, g.Detail))
            .OrderBy(g => g.Key.Gate);

        foreach (var group in deferred)
        {
            text.Append("| every screen (").Append(group.Count()).Append(" captures) | both | ")
                .Append(group.Key.Gate).Append(" | ").Append(group.Key.Verdict.ToString().ToUpperInvariant()).Append(" | ")
                .Append(Escape(group.Key.Detail)).AppendLine(" |");
        }

        text.AppendLine();
        text.AppendLine("## Declared deltas");
        text.AppendLine();
        text.AppendLine(deltas.Note);
        text.AppendLine();
        text.AppendLine("| Delta | Region | Screens | Exercised | Reason |");
        text.AppendLine("|---|---|---|---|---|");

        foreach (var delta in deltas.Deltas)
        {
            text.Append("| ").Append(delta.Id).Append(" | ").Append(delta.Region).Append(" | ")
                .Append(string.Join(", ", delta.Screens)).Append(" | ")
                .Append(delta.Deferred ? "deferred" : "yes").Append(" | ")
                .Append(Escape(delta.Deferred ? delta.Reason + " " + delta.DeferredReason : delta.Reason))
                .AppendLine(" |");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
    }

    private static string Cell(ScreenOutcome outcome, string gate)
    {
        var result = outcome.Gates.FirstOrDefault(g => g.Gate == gate);

        return result is null ? "n/a" : result.Verdict switch
        {
            GateVerdict.Pass => "pass",
            GateVerdict.Fail => "FAIL",
            GateVerdict.Delta => "delta",
            _ => "deferred",
        };
    }

    private static string Link(string folder, string file) =>
        File.Exists(Path.Combine(folder, file))
            ? $"[{file}](../parity-out/{Path.GetFileName(Path.GetDirectoryName(folder))}/{Path.GetFileName(folder)}/{file})"
            : "absent";

    private static string Escape(string value) => value.Replace("|", "\\|");
}
