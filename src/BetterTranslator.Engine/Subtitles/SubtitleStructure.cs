using System.Text.RegularExpressions;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Verification.Structure;

namespace BetterTranslator.Engine.Subtitles;

public static partial class SubtitleSyntax
{
    [GeneratedRegex(@"^\s*(?<start>(?:\d{1,2}:)?\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>(?:\d{1,2}:)?\d{2}:\d{2}[,.]\d{1,3})(?<settings>.*)$")]
    internal static partial Regex Timing { get; }

    public static bool LooksLikeSubtitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var seen = 0;

        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (Timing.IsMatch(line))
            {
                return true;
            }

            if (++seen >= 6)
            {
                break;
            }
        }

        return false;
    }
}

public sealed class SubtitleStructure : IFormatAdapter
{
    public const string FormatName = "subtitle";

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    public static SubtitleStructure Instance { get; } = new();

    public string Format => FormatName;

    public DocumentModel Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var nodes = new List<DocumentNode>
        {
            new(DocumentModel.RootPath, string.Empty, DocumentNodeKind.Document, new CheckRange(DocumentModel.RootPath, 0, text.Length), 0, NoAttributes),
        };

        var chunks = new List<ChunkNode>();
        var invariants = new List<InvariantToken>();
        var lines = Lines(text);
        var ordinal = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var timing = SubtitleSyntax.Timing.Match(Text(text, lines[i]));

            if (!timing.Success)
            {
                continue;
            }

            var cueStart = lines[i].Start;
            var index = string.Empty;

            if (i > 0 && !IsBlank(text, lines[i - 1]) && !SubtitleSyntax.Timing.IsMatch(Text(text, lines[i - 1])))
            {
                index = Text(text, lines[i - 1]).Trim();
                cueStart = lines[i - 1].Start;
            }

            var textStart = i + 1 < lines.Count ? lines[i + 1].Start : lines[i].End;
            var last = i;

            for (var j = i + 1; j < lines.Count && !IsBlank(text, lines[j]); j++)
            {
                last = j;
            }

            var textEnd = last > i ? lines[last].End : textStart;
            var path = "/cue/" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var identity = index.Length > 0 ? index : (ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

            nodes.Add(new DocumentNode(
                path,
                DocumentModel.RootPath,
                DocumentNodeKind.Cue,
                new CheckRange(path, cueStart, lines[last].End - cueStart),
                1,
                new Dictionary<string, string>
                {
                    ["index"] = identity,
                    ["start"] = timing.Groups["start"].Value,
                    ["end"] = timing.Groups["end"].Value,
                    ["settings"] = timing.Groups["settings"].Value.Trim(),
                    ["lines"] = (last - i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));

            var textRange = new CheckRange(path, textStart, Math.Max(0, textEnd - textStart));
            chunks.Add(new ChunkNode("cue:" + identity, "cue", textRange));

            if (textRange.Length > 0)
            {
                invariants.AddRange(InvariantScanner.Scan(text.Substring(textRange.Offset, textRange.Length), path, textRange.Offset));
            }

            ordinal++;
            i = last;
        }

        return new DocumentModel
        {
            Format = FormatName,
            Length = text.Length,
            Nodes = nodes,
            Chunks = chunks,
            Invariants = invariants,
            Placeholders = [.. PlaceholderGuard.Residue(text)
                .Select(r => new PlaceholderResidue(new CheckRange(DocumentModel.RootPath, r.Index, r.Length), r.Text))],
        };
    }

    private static List<(int Start, int End)> Lines(string text)
    {
        var lines = new List<(int Start, int End)>();
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n')
            {
                continue;
            }

            var end = i;

            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            lines.Add((start, end));
            start = i + 1;
        }

        return lines;
    }

    private static string Text(string text, (int Start, int End) line) => text[line.Start..line.End];

    private static bool IsBlank(string text, (int Start, int End) line)
    {
        for (var i = line.Start; i < line.End; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
