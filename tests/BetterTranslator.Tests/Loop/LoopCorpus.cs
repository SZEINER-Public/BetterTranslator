using System.Text.RegularExpressions;

namespace BetterTranslator.Tests.Loop;

public sealed record CorpusSlice(string Name, DocumentUnderTest Document, int Sections);

public sealed record CorpusSplit(CorpusSlice Tuning, CorpusSlice HeldOut, int TotalSections);

public static class LoopCorpus
{
    public const int HeldOutEvery = 5;

    private static readonly Regex HeadingLine = new(@"^\s{0,3}#{1,6}\s", RegexOptions.Compiled);

    private static readonly Regex FenceLine = new(@"^\s*```", RegexOptions.Compiled);

    public static CorpusSplit Split(DocumentUnderTest source, int tuningSections, int heldOutSections)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (frontMatter, sections) = Sections(source.Text);

        var tuning = new List<string>();
        var heldOut = new List<string>();

        for (var i = 0; i < sections.Count; i++)
        {
            if ((i + 1) % HeldOutEvery == 0)
            {
                heldOut.Add(sections[i]);
            }
            else
            {
                tuning.Add(sections[i]);
            }
        }

        return new CorpusSplit(
            Assemble("tuning", source, frontMatter, Take(tuning, tuningSections)),
            Assemble("held-out", source, frontMatter, Take(heldOut, heldOutSections)),
            sections.Count);
    }

    private static List<string> Take(List<string> sections, int wanted)
    {
        if (wanted <= 0 || wanted >= sections.Count)
        {
            return sections;
        }

        var picked = new List<string>(wanted);

        for (var i = 0; i < wanted; i++)
        {
            picked.Add(sections[i * sections.Count / wanted]);
        }

        return picked;
    }

    private static CorpusSlice Assemble(
        string name,
        DocumentUnderTest source,
        string frontMatter,
        List<string> sections)
    {
        var body = string.Join("\n\n", sections);
        var text = frontMatter.Length == 0 ? body : frontMatter + "\n\n" + body;

        if (!text.EndsWith('\n'))
        {
            text += "\n";
        }

        return new CorpusSlice(name, new DocumentUnderTest(text, source.HasByteOrderMark, source.Newline), sections.Count);
    }

    public static (string FrontMatter, List<string> Sections) Sections(string markdown)
    {
        var lines = markdown.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var start = 0;
        var frontMatter = string.Empty;

        if (lines.Length > 1 && lines[0].TrimEnd() == "---")
        {
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() == "---")
                {
                    frontMatter = string.Join("\n", lines[..(i + 1)]);
                    start = i + 1;
                    break;
                }
            }
        }

        var sections = new List<string>();
        var current = new List<string>();
        var inFence = false;

        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];

            if (FenceLine.IsMatch(line))
            {
                inFence = !inFence;
            }

            if (!inFence && HeadingLine.IsMatch(line) && current.Any(l => l.Trim().Length > 0))
            {
                sections.Add(string.Join("\n", current).Trim('\n'));
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Any(l => l.Trim().Length > 0))
        {
            sections.Add(string.Join("\n", current).Trim('\n'));
        }

        return (frontMatter, sections);
    }
}
