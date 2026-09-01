using System.Text.RegularExpressions;

namespace BetterTranslator.Mac.Parity;

public enum GateVerdict
{
    Pass,
    Fail,
    Deferred,
    Delta,
}

public sealed record GateResult(string Gate, GateVerdict Verdict, string Detail);

public static partial class Gates
{
    public const double BoundsTolerance = 2.0;

    public const double PixelTolerance = 0.02;

    [GeneratedRegex(@"\b(cuda|vulkan|metal|gpu|nvidia|cublas|graphics\s+card|accelerator|geforce|radeon)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenWords();

    public static GateResult String(TreeRecord wrapper, IEnumerable<string> resourceValues, IReadOnlyList<string> sourceOffences)
    {
        var offences = new List<string>();

        Collect(wrapper.Root, offences);

        foreach (var value in resourceValues)
        {
            var match = ForbiddenWords().Match(value);

            if (match.Success)
            {
                offences.Add($"resource value \"{value}\" names \"{match.Value}\"");
            }
        }

        offences.AddRange(sourceOffences);

        return offences.Count == 0
            ? new GateResult("string", GateVerdict.Pass,
                "no rendered string, resource value or authored source line in the wrapper names an accelerator")
            : new GateResult("string", GateVerdict.Fail, string.Join("; ", offences.Take(8)));
    }

    public static IReadOnlyList<string> ScanSource(string macFolder)
    {
        string[] roots = ["src", "tools", "proj"];
        var offences = new List<string>();

        foreach (var root in roots)
        {
            var folder = Path.Combine(macFolder, root);

            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!Scannable(file))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);

                for (var i = 0; i < lines.Length; i++)
                {
                    var match = ForbiddenWords().Match(lines[i]);

                    if (match.Success)
                    {
                        offences.Add($"{Path.GetRelativePath(macFolder, file)}:{i + 1} names \"{match.Value}\"");
                    }
                }
            }
        }

        return offences;
    }

    private static bool Scannable(string file)
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.GetFileName(file) is "Gates.cs" or "deltas.json")
        {
            return false;
        }

        return Path.GetExtension(file) is ".cs" or ".axaml" or ".xaml" or ".resx" or ".json";
    }

    private static void Collect(NodeRecord node, List<string> offences)
    {
        if (node.Text is { Length: > 0 } text)
        {
            var match = ForbiddenWords().Match(text);

            if (match.Success)
            {
                offences.Add($"{node.Role} renders \"{text}\" which names \"{match.Value}\"");
            }
        }

        foreach (var child in node.Children)
        {
            Collect(child, offences);
        }
    }

    public static GateResult Structural(TreeRecord wrapper, TreeRecord? reference)
    {
        if (reference is null)
        {
            return new GateResult("structural", GateVerdict.Deferred, "no reference tree on this host");
        }

        var problems = new List<string>();

        Compare(wrapper.Root, reference.Root, "root", problems);

        return problems.Count == 0
            ? new GateResult("structural", GateVerdict.Pass, "same roles in the same order and z-order, bounds inside tolerance")
            : new GateResult("structural", GateVerdict.Fail, string.Join("; ", problems.Take(8)));
    }

    private static void Compare(NodeRecord left, NodeRecord right, string path, List<string> problems)
    {
        if (!string.Equals(left.Role, right.Role, StringComparison.Ordinal))
        {
            problems.Add($"{path}: role {left.Role} against {right.Role}");
            return;
        }

        if (left.Order != right.Order || left.ZIndex != right.ZIndex)
        {
            problems.Add($"{path}: order {left.Order}/{left.ZIndex} against {right.Order}/{right.ZIndex}");
        }

        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(left.Bounds[i] - right.Bounds[i]) > BoundsTolerance)
            {
                problems.Add($"{path}: bounds[{i}] {left.Bounds[i]} against {right.Bounds[i]}");
                break;
            }
        }

        if (left.Children.Count != right.Children.Count)
        {
            problems.Add($"{path}: {left.Children.Count} children against {right.Children.Count}");
            return;
        }

        for (var i = 0; i < left.Children.Count; i++)
        {
            Compare(left.Children[i], right.Children[i], path + "/" + left.Children[i].Role, problems);
        }
    }

    public static GateResult Token(TreeRecord wrapper, TreeRecord? reference)
    {
        if (reference is null)
        {
            return new GateResult("token", GateVerdict.Deferred, "no reference tree on this host");
        }

        var problems = new List<string>();

        CompareTokens(wrapper.Root, reference.Root, "root", problems);

        return problems.Count == 0
            ? new GateResult("token", GateVerdict.Pass, "every foreground and background resolves to the same named token")
            : new GateResult("token", GateVerdict.Fail, string.Join("; ", problems.Take(8)));
    }

    private static void CompareTokens(NodeRecord left, NodeRecord right, string path, List<string> problems)
    {
        if (!string.Equals(left.ForegroundToken, right.ForegroundToken, StringComparison.Ordinal))
        {
            problems.Add($"{path}: foreground {left.ForegroundToken} against {right.ForegroundToken}");
        }

        if (!string.Equals(left.BackgroundToken, right.BackgroundToken, StringComparison.Ordinal))
        {
            problems.Add($"{path}: background {left.BackgroundToken} against {right.BackgroundToken}");
        }

        var pairs = Math.Min(left.Children.Count, right.Children.Count);

        for (var i = 0; i < pairs; i++)
        {
            CompareTokens(left.Children[i], right.Children[i], path + "/" + left.Children[i].Role, problems);
        }
    }

    public static GateResult Text(TreeRecord wrapper, TreeRecord? reference)
    {
        if (reference is null)
        {
            return new GateResult("text", GateVerdict.Deferred, "no reference tree on this host");
        }

        var left = new List<string>();
        var right = new List<string>();

        CollectText(wrapper.Root, left);
        CollectText(reference.Root, right);

        var differences = new List<string>();

        if (left.Count != right.Count)
        {
            differences.Add($"{left.Count} strings against {right.Count}");
        }

        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                differences.Add($"\"{left[i]}\" against \"{right[i]}\"");
            }
        }

        return differences.Count == 0
            ? new GateResult("text", GateVerdict.Pass, $"{left.Count} strings identical")
            : new GateResult("text", GateVerdict.Fail, string.Join("; ", differences.Take(8)));
    }

    private static void CollectText(NodeRecord node, List<string> into)
    {
        if (node.Text is { Length: > 0 } text)
        {
            into.Add(text);
        }

        foreach (var child in node.Children)
        {
            CollectText(child, into);
        }
    }
}
