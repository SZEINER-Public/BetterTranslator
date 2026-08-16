using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterTranslator.Tests.Loop;

public static class TranslationChecker
{
    public const int ResidueMinimumWords = 6;

    public const double MinimumUnitRatio = 0.35;

    public const double MaximumUnitRatio = 2.4;

    private static readonly Regex HeadingLine = new(@"^\s{0,3}(#{1,6})\s", RegexOptions.Compiled);

    private static readonly Regex ListLine = new(@"^\s*([-*+]|\d+[.)])\s", RegexOptions.Compiled);

    private static readonly Regex TableLine = new(@"^\s*\|", RegexOptions.Compiled);

    private static readonly Regex FenceLine = new(@"^\s*```", RegexOptions.Compiled);

    private static readonly Regex SentenceBreak = new(@"(?<=[.!?;:])\s+", RegexOptions.Compiled);

    private static readonly Regex Word =
        new(@"[\p{L}\p{Nd}]+(?:['’-][\p{L}\p{Nd}]+)*", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex CodeSpan = new(@"`[^`\n]+`", RegexOptions.Compiled);

    private static readonly Regex FencedBlock = new(@"(?s)```.*?```", RegexOptions.Compiled);

    private static readonly Regex LinkTarget = new(@"\]\(([^)\s]+)", RegexOptions.Compiled);

    private static readonly Regex WindowsPath = new(@"[A-Za-z]:\\\S+", RegexOptions.Compiled);

    private static readonly Regex RelativePath = new(@"(?<![\w/\\])[\w.-]+(?:/[\w.-]+)+/?", RegexOptions.Compiled);

    private static readonly Regex Version = new(@"\bv?\d+\.\d+(?:\.\d+)*\b", RegexOptions.Compiled);

    private static readonly Regex NumericToken = new(@"\d+(?:\.\d+)*", RegexOptions.Compiled);

    private static readonly Regex WordBeforeBacktick = new(@"\w`", RegexOptions.Compiled);

    private static readonly Regex WordAfterBacktick = new(@"`\w", RegexOptions.Compiled);

    private static readonly Regex WordBeforeBold = new(@"\w\*\*", RegexOptions.Compiled);

    private static readonly Regex WordAfterBold = new(@"\*\*\w", RegexOptions.Compiled);

    private static readonly Regex WordAfterLink = new(@"\)\w", RegexOptions.Compiled);

    private static readonly Regex Typographic = new(@"[‘’“”–—…]", RegexOptions.Compiled);

    private static readonly Regex BoldMarker = new(@"\*\*", RegexOptions.Compiled);

    private static readonly Regex ItalicMarker = new(@"(?<!\*)\*(?!\*)", RegexOptions.Compiled);

    private static readonly Regex TrailingSpace = new(@"[ \t]+$", RegexOptions.Compiled);

    private static readonly Regex FrontMatterField = new(@"^([A-Za-z0-9_.\-]+):[ \t]+(\S.*?)[ \t]*$", RegexOptions.Compiled);

    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"^[\w.\-/\\]+$", RegexOptions.Compiled);

    private const string CzechLetters = "aábcčdďeéěfghiíjklmnňoópqrřsštťuúůvwxyýzž";

    private static readonly string[] CzechMarkers =
    [
        "a", "se", "na", "je", "že", "v", "o", "s", "do", "ve", "pro", "které", "která", "který",
        "jako", "ale", "po", "při", "za", "od", "to", "co", "aby", "jsou", "byl", "byla", "není",
        "tak", "už", "když", "nebo", "této", "toho", "tím", "jen", "má", "ma", "své", "svůj",
    ];

    private static readonly string[] EnglishMarkers =
    [
        "the", "and", "of", "to", "in", "is", "that", "for", "with", "are", "this", "it", "as",
        "be", "on", "by", "not", "or", "from", "which", "was", "were", "has", "have", "but",
        "they", "their", "its", "an", "at", "so", "than", "then", "when", "what", "who",
    ];

    public static TranslationMetrics Score(
        DocumentUnderTest source,
        DocumentUnderTest candidate,
        IReadOnlyList<string>? productNames = null,
        int runtimeFailures = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);

        var src = source.Text;
        var cand = candidate.Text;

        var srcLines = Lines(src);
        var candLines = Lines(cand);

        var srcHeadings = Headings(srcLines);
        var candHeadings = Headings(candLines);

        var srcBlocks = Blocks(srcLines);
        var candBlocks = Blocks(candLines);

        var names = productNames ?? ProductNames();

        var boundary = BoundaryExcess(src, cand);

        var sourceSentences = ResidueCandidates(srcLines);
        var normalisedCandidate = Whitespace.Replace(cand, " ");

        var residue = sourceSentences.Count(s => normalisedCandidate.Contains(s, StringComparison.Ordinal));

        var (unitsMissing, unitsOutOfBand) = Completeness(srcBlocks, candBlocks);

        return new TranslationMetrics
        {
            SourceCharacters = src.Length,
            CandidateCharacters = cand.Length,
            SourceHeadings = srcHeadings.Count,
            SourceUnits = srcBlocks.Count,

            CodeSpansLost = Deficit(src, cand, CodeSpan),
            FencedBlocksLost = Deficit(src, cand, FencedBlock),
            LinkTargetsLost = DeficitGroup(src, cand, LinkTarget),
            PathsLost = Deficit(src, cand, WindowsPath) + Deficit(src, cand, RelativePath),
            VersionsLost = Deficit(src, cand, Version),
            NumericTokensLost = Deficit(src, cand, NumericToken),
            ProductNamesLost = NamesLost(src, cand, names),
            ProtectedTokensInvented = Surplus(src, cand, CodeSpan) + SurplusGroup(src, cand, LinkTarget),

            UnitsMissing = unitsMissing,
            UnitsOutOfBand = unitsOutOfBand,

            ResidualSourceSentences = residue,
            UnitsNotInTargetLanguage = candBlocks.Count(b => !LooksCzech(b)),

            HeadingCountDelta = Math.Abs(srcHeadings.Count - candHeadings.Count),
            HeadingLevelMismatches = LevelMismatches(srcHeadings, candHeadings),
            HeadingsUntranslated = Untranslated(srcHeadings, candHeadings),
            FrontMatterProseUntranslated = FrontMatterUntranslated(srcLines, candLines),
            ListMarkerDelta = Math.Abs(Count(srcLines, ListLine) - Count(candLines, ListLine)),
            TableRowDelta = Math.Abs(Count(srcLines, TableLine) - Count(candLines, TableLine)),
            ParagraphDelta = Math.Abs(srcBlocks.Count - candBlocks.Count),
            BlankLineTopologyMismatches = Math.Abs(Blank(srcLines) - Blank(candLines)),
            BoldMarkerDelta = Math.Abs(BoldMarker.Matches(src).Count - BoldMarker.Matches(cand).Count),
            ItalicMarkerDelta = Math.Abs(ItalicMarker.Matches(src).Count - ItalicMarker.Matches(cand).Count),

            ByteOrderMarkLost = source.HasByteOrderMark && !candidate.HasByteOrderMark ? 1 : 0,
            LineEndingChanged = string.Equals(source.Newline, candidate.Newline, StringComparison.Ordinal) ? 0 : 1,
            TrailingWhitespaceIntroduced = Math.Max(0, Trailing(candLines) - Trailing(srcLines)),
            TypographicSubstitutions = Math.Max(0, Typographic.Matches(cand).Count - Typographic.Matches(src).Count),
            EncodingFaults = EncodingFaults(src, cand),

            BoundaryFusionExcess = boundary,
            SourceWordBeforeBacktick = WordBeforeBacktick.Matches(src).Count,
            SourceWordAfterBacktick = WordAfterBacktick.Matches(src).Count,
            CandidateWordBeforeBacktick = WordBeforeBacktick.Matches(cand).Count,
            CandidateWordAfterBacktick = WordAfterBacktick.Matches(cand).Count,

            TranslatedHeadingRate = srcHeadings.Count == 0
                ? 100
                : 100d * (srcHeadings.Count - Untranslated(srcHeadings, candHeadings)) / srcHeadings.Count,
            TerminologyConsistency = Terminology(src, cand, names),
            MorphologicalLegality = Morphology(cand),
            FluencyProxy = Fluency(src, cand, residue, sourceSentences.Count),

            RuntimeFailures = runtimeFailures,
        };
    }

    public static IReadOnlyList<string> ProductNames()
    {
        try
        {
            var lists = BetterTranslator.Engine.Slop.DoNotTranslate.Load();
            return [.. lists.Strict.Concat(lists.Declinable).Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string[] Lines(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

    private static int Count(string[] lines, Regex pattern) => lines.Count(pattern.IsMatch);

    private static int Blank(string[] lines) => lines.Count(l => l.Trim().Length == 0);

    private static int Trailing(string[] lines) => lines.Count(TrailingSpace.IsMatch);

    private static List<(int Level, string Text)> Headings(string[] lines)
    {
        var found = new List<(int, string)>();
        var inFence = false;

        foreach (var line in lines)
        {
            if (FenceLine.IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var match = HeadingLine.Match(line);

            if (match.Success)
            {
                found.Add((match.Groups[1].Value.Length, line));
            }
        }

        return found;
    }

    private static List<string> Blocks(string[] lines)
    {
        var blocks = new List<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                if (current.Count > 0)
                {
                    blocks.Add(string.Join("\n", current));
                    current.Clear();
                }

                continue;
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            blocks.Add(string.Join("\n", current));
        }

        return blocks;
    }

    private static Dictionary<string, int> Bag(string text, Regex pattern)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match match in pattern.Matches(text))
        {
            bag[match.Value] = bag.GetValueOrDefault(match.Value) + 1;
        }

        return bag;
    }

    private static Dictionary<string, int> BagGroup(string text, Regex pattern)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match match in pattern.Matches(text))
        {
            var key = match.Groups[1].Value;
            bag[key] = bag.GetValueOrDefault(key) + 1;
        }

        return bag;
    }

    private static int Deficit(string source, string candidate, Regex pattern) =>
        Deficit(Bag(source, pattern), Bag(candidate, pattern));

    private static int DeficitGroup(string source, string candidate, Regex pattern) =>
        Deficit(BagGroup(source, pattern), BagGroup(candidate, pattern));

    private static int Surplus(string source, string candidate, Regex pattern) =>
        Deficit(Bag(candidate, pattern), Bag(source, pattern));

    private static int SurplusGroup(string source, string candidate, Regex pattern) =>
        Deficit(BagGroup(candidate, pattern), BagGroup(source, pattern));

    private static int Deficit(Dictionary<string, int> wanted, Dictionary<string, int> got) =>
        wanted.Sum(pair => Math.Max(0, pair.Value - got.GetValueOrDefault(pair.Key)));

    private static int NamesLost(string source, string candidate, IReadOnlyList<string> names)
    {
        var lost = 0;

        foreach (var name in names.Where(n => n.Length > 1))
        {
            var pattern = new Regex(@"(?<![\p{L}\p{Nd}])" + Regex.Escape(name) + @"(?![\p{L}\p{Nd}])");
            lost += Math.Max(0, pattern.Matches(source).Count - pattern.Matches(candidate).Count);
        }

        return lost;
    }

    private static int LevelMismatches(List<(int Level, string Text)> source, List<(int Level, string Text)> candidate)
    {
        var shared = Math.Min(source.Count, candidate.Count);
        var mismatches = 0;

        for (var i = 0; i < shared; i++)
        {
            if (source[i].Level != candidate[i].Level)
            {
                mismatches++;
            }
        }

        return mismatches;
    }

    private static int Untranslated(List<(int Level, string Text)> source, List<(int Level, string Text)> candidate)
    {
        var present = new HashSet<string>(candidate.Select(h => h.Text), StringComparer.Ordinal);

        return source.Count(h => present.Contains(h.Text));
    }

    private static (int Missing, int OutOfBand) Completeness(List<string> source, List<string> candidate)
    {
        var missing = 0;
        var outOfBand = 0;

        for (var i = 0; i < source.Count; i++)
        {
            if (i >= candidate.Count || candidate[i].Trim().Length == 0)
            {
                missing++;
                continue;
            }

            var wanted = (double)source[i].Length;

            if (wanted < 1)
            {
                continue;
            }

            var ratio = candidate[i].Length / wanted;

            if (ratio < MinimumUnitRatio || ratio > MaximumUnitRatio)
            {
                outOfBand++;
            }
        }

        return (missing, outOfBand);
    }

    private static List<string> ResidueCandidates(string[] lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();

        foreach (var line in lines)
        {
            var normalised = Whitespace.Replace(line, " ").Trim();

            if (normalised.Length == 0)
            {
                continue;
            }

            foreach (var piece in SentenceBreak.Split(normalised))
            {
                var sentence = piece.Trim();

                if (sentence.Length == 0 || Word.Matches(sentence).Count < ResidueMinimumWords)
                {
                    continue;
                }

                if (seen.Add(sentence))
                {
                    found.Add(sentence);
                }
            }
        }

        return found;
    }

    private static int BoundaryExcess(string source, string candidate)
    {
        Regex[] classes =
        [
            WordBeforeBacktick, WordAfterBacktick, WordBeforeBold, WordAfterBold, WordAfterLink,
        ];

        return classes.Sum(c => Math.Max(0, c.Matches(candidate).Count - c.Matches(source).Count));
    }

    private static int FrontMatterUntranslated(string[] source, string[] candidate)
    {
        var wanted = FrontMatterProse(source);

        if (wanted.Count == 0)
        {
            return 0;
        }

        var present = new HashSet<string>(FrontMatterProse(candidate), StringComparer.Ordinal);

        return wanted.Count(v => present.Contains(v));
    }

    private static List<string> FrontMatterProse(string[] lines)
    {
        var values = new List<string>();

        if (lines.Length < 2 || lines[0].TrimEnd() != "---")
        {
            return values;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---")
            {
                break;
            }

            var match = FrontMatterField.Match(lines[i]);

            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups[2].Value;

            if (IsProse(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    public static bool IsProse(string value)
    {
        if (value.Length == 0 || "[{&*|>\"'".Contains(value[0], StringComparison.Ordinal))
        {
            return false;
        }

        return !IsoDate.IsMatch(value)
            && !Identifier.IsMatch(value)
            && Word.Matches(value).Count >= ResidueMinimumWords;
    }

    private static int EncodingFaults(string source, string candidate)
    {
        var faults = Math.Max(0, candidate.Count(c => c == '�') - source.Count(c => c == '�'));
        var strict = new UTF8Encoding(false, true);

        try
        {
            if (strict.GetString(strict.GetBytes(candidate)) != candidate)
            {
                faults++;
            }
        }
        catch (Exception ex) when (ex is EncoderFallbackException or DecoderFallbackException)
        {
            faults++;
        }

        return faults;
    }

    private static bool LooksCzech(string block)
    {
        var words = Word.Matches(block).Select(m => m.Value.ToLowerInvariant()).ToList();

        if (words.Count < ResidueMinimumWords)
        {
            return true;
        }

        var czech = words.Count(w => CzechMarkers.Contains(w, StringComparer.Ordinal));
        var english = words.Count(w => EnglishMarkers.Contains(w, StringComparer.Ordinal));
        var diacritics = block.Any(c => "áčďéěíňóřšťúůýžÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ".Contains(c, StringComparison.Ordinal));

        return czech > english || (czech == english && diacritics);
    }

    private static double Terminology(string source, string candidate, IReadOnlyList<string> names)
    {
        var wanted = 0;
        var kept = 0;

        foreach (var name in names.Where(n => n.Length > 1))
        {
            var pattern = new Regex(@"(?<![\p{L}\p{Nd}])" + Regex.Escape(name) + @"(?![\p{L}\p{Nd}])");
            var inSource = pattern.Matches(source).Count;

            if (inSource == 0)
            {
                continue;
            }

            wanted += inSource;
            kept += Math.Min(inSource, pattern.Matches(candidate).Count);
        }

        return wanted == 0 ? 100 : 100d * kept / wanted;
    }

    private static double Morphology(string candidate)
    {
        var words = Word.Matches(candidate)
            .Select(m => m.Value)
            .Where(w => w.All(char.IsLetter))
            .ToList();

        if (words.Count == 0)
        {
            return 100;
        }

        var legal = words.Count(w => w.ToLowerInvariant().All(c => CzechLetters.Contains(c, StringComparison.Ordinal)));

        return 100d * legal / words.Count;
    }

    private static double Fluency(string source, string candidate, int residue, int sentences)
    {
        var residuePenalty = sentences == 0 ? 0 : 100d * residue / sentences;

        var words = Word.Matches(candidate).Select(m => m.Value).ToList();
        var repeats = 0;

        for (var i = 2; i < words.Count; i++)
        {
            if (string.Equals(words[i], words[i - 1], StringComparison.Ordinal)
                && string.Equals(words[i], words[i - 2], StringComparison.Ordinal))
            {
                repeats++;
            }
        }

        var repeatPenalty = words.Count == 0 ? 0 : 100d * repeats / words.Count;

        var ratio = source.Length == 0 ? 1 : (double)candidate.Length / source.Length;
        var lengthPenalty = Math.Min(40d, Math.Abs(ratio - 1.10) * 100d);

        return Math.Clamp(100 - residuePenalty - repeatPenalty - lengthPenalty, 0, 100);
    }
}
