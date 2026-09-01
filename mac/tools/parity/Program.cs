using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using System.Text.Json;

namespace BetterTranslator.Mac.Parity;

public static class Program
{
    private const string RepoRelativeOutput = "parity-out";

    public static int Main(string[] args)
    {
        var macFolder = MacFolder();
        var output = Path.Combine(macFolder, RepoRelativeOutput);

        AppBuilder.Configure<Mac.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        SeededContent.Prepare(output);

        return Run(macFolder, output);
    }

    private static int Run(string macFolder, string output)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("The headless application did not start.");

        var tokens = TokenIndex.Build(application);
        var deltas = DeltaFile.Load(Path.Combine(AppContext.BaseDirectory, "deltas.json"));
        var screens = Screens.All();
        var sourceOffences = Gates.ScanSource(macFolder);

        var registered = screens.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var stale = deltas.Deltas
            .SelectMany(d => d.Screens)
            .Where(s => s != "*" && !registered.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (stale.Count > 0)
        {
            Console.Error.WriteLine("deltas.json names screens that are not registered: " + string.Join(", ", stale));
            return 2;
        }
        var outcomes = new List<ScreenOutcome>();
        var failures = 0;

        foreach (var screen in screens)
        {
            foreach (var size in LogicalSize.All)
            {
                var folder = Path.Combine(output, screen.Id, size.Id);
                var capture = Capture.Render(screen, size, tokens, folder);

                WriteTree(Path.Combine(folder, "mac.tree.json"), capture.Tree);

                var reference = ReadTree(Path.Combine(folder, "windows.tree.json"));
                var diff = Diff.Write(Path.Combine(folder, "windows.png"), capture.PngPath, Path.Combine(folder, "diff.png"));
                var applied = deltas.For(screen.Id);

                var gates = new List<GateResult>
                {
                    Gates.Structural(capture.Tree, reference),
                    Gates.Token(capture.Tree, reference),
                    Gates.Text(capture.Tree, reference),
                    Gates.String(capture.Tree, ResourceStrings(application), sourceOffences),
                    Pixel(diff, applied),
                };

                failures += gates.Count(g => g.Verdict == GateVerdict.Fail);
                outcomes.Add(new ScreenOutcome(screen.Id, size.Id, gates, diff, applied, folder));
            }
        }

        Report.Write(Path.Combine(macFolder, "reports", "parity.md"), outcomes, deltas, HostNote());

        Console.Out.WriteLine($"screens={screens.Count} captures={outcomes.Count} failures={failures}");

        return failures == 0 ? 0 : 1;
    }

    private static GateResult Pixel(DiffOutcome diff, IReadOnlyList<DeltaEntry> applied)
    {
        if (!diff.Produced)
        {
            return new GateResult("pixel", GateVerdict.Deferred, diff.Detail);
        }

        if (applied.Any(d => d.Screens.Contains("*")))
        {
            return new GateResult("pixel", GateVerdict.Delta,
                $"{diff.Detail}; excluded by the declared deltas {string.Join(", ", applied.Select(d => d.Id))}");
        }

        return diff.Ratio <= Gates.PixelTolerance
            ? new GateResult("pixel", GateVerdict.Pass, diff.Detail)
            : new GateResult("pixel", GateVerdict.Fail, diff.Detail);
    }

    private static IEnumerable<string> ResourceStrings(Application application)
    {
        foreach (var key in application.Resources.Keys)
        {
            yield return key.ToString() ?? string.Empty;

            if (application.Resources.TryGetValue(key, out var value) && value is string text)
            {
                yield return text;
            }
        }
    }

    private static void WriteTree(string path, TreeRecord tree) =>
        File.WriteAllText(path, JsonSerializer.Serialize(tree, TreeOptions));

    private static TreeRecord? ReadTree(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<TreeRecord>(File.ReadAllText(path), TreeOptions) : null;

    private static readonly JsonSerializerOptions TreeOptions = new() { WriteIndented = true };

    private static string HostNote() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} on {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}. "
        + "The Windows head needs WPF and a Windows host, so windows.png and windows.tree.json are produced by mac/tools/parity-windows and are absent here.";

    private static string MacFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.Name != "mac")
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The mac folder could not be located from " + AppContext.BaseDirectory + ".");
    }
}
