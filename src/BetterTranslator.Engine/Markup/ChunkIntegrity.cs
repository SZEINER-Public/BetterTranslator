using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Compares a protected source chunk against the model's answer. Ported from
/// `Test-ChunkIntegrity` and `Remove-TranslatorNote`.
///
/// Returns null when the chunk is acceptable, otherwise a short reason. The
/// caller retries once on a reason and keeps the SOURCE text on a second
/// failure -- a readable original beats a corrupted translation.
///
/// The order of the checks is the reference's and is part of the contract: the
/// first thing that fails is the reason reported, and callers log that string.
/// </summary>
public static class ChunkIntegrity
{
    /// <summary>
    /// Catches silent truncation, which is what removed 285 lines from that
    /// README. A legitimate Czech translation of English prose runs longer, not
    /// 40% shorter, so a large collapse is a dropped chunk rather than a terse
    /// style.
    /// </summary>
    public const double DefaultMinLengthRatio = 0.55;

    private static readonly Regex Backtick = new("`", RegexOptions.Compiled);
    private static readonly Regex LongOption = new("(?<![A-Za-z0-9-])--[a-z][a-z0-9-]*", RegexOptions.Compiled);
    private static readonly Regex UrlScheme = new("https?://", RegexOptions.Compiled);
    private static readonly Regex Pipe = new(@"\|", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LineMarker = new(@"^\s*(#{1,6}\s|[-*+]\s|\d+\.\s|>\s)", RegexOptions.Compiled);

    /// <summary>
    /// The answer without the code spans it invented, when the source had none.
    ///
    /// Repair rather than relaxation: a model handed a line of CLI syntax comes
    /// back having helpfully wrapped every flag and placeholder in backticks, and
    /// the count check -- rightly -- refuses it, because losing a backtick pair
    /// turns code into prose. But a source with no backticks has no code span to
    /// lose, so the added ones are formatting the answer was never asked for.
    /// Removing them yields the answer that should have come back, which is
    /// strictly better than admitting the artifact or discarding the translation.
    ///
    /// Measured: every correct Czech translation of "Default output naming:
    /// &lt;name&gt;.&lt;to&gt;.&lt;ext&gt; beside the input unless --out is given."
    /// was refused with "backtick count changed: 0 in source, 6 in output", so
    /// the line shipped in English.
    ///
    /// Does nothing when the source has backticks of its own: there the counts
    /// carry real information and a mismatch is damage.
    /// </summary>
    public static string DropInventedCodeSpans(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);

        return string.IsNullOrEmpty(translated) || source.Contains('`', StringComparison.Ordinal)
            ? translated
            : translated.Replace("`", string.Empty, StringComparison.Ordinal);
    }

    /// <param name="allowAddedBrackets">
    /// Lets an answer add a balanced pair of brackets where the source had none.
    ///
    /// Off for a document chunk: a bracket that appears there is usually a link
    /// or a marker the model manufactured. On for a standalone sentence, where it
    /// is a translator's clarification -- measured, "i18n resource files" came
    /// back as Czech with "(internacionalizace)" spelled out after it, which is a
    /// good translation and was refused as damage. Only ever a balanced pair, and
    /// only where the source had none of that bracket at all.
    /// </param>
    public static string? Check(
        string source,
        string? translated,
        IReadOnlyList<string>? doNotTranslate = null,
        IReadOnlyList<string>? declinable = null,
        double minLengthRatio = DefaultMinLengthRatio,
        bool allowAddedBrackets = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(translated))
        {
            return "empty answer";
        }

        doNotTranslate ??= [];
        declinable ??= [];

        var sentinels = CheckSentinels(source, translated);
        if (sentinels is not null)
        {
            return sentinels;
        }

        var placeholders = CheckPlaceholders(source, translated);
        if (placeholders is not null)
        {
            return placeholders;
        }

        // Inline code spans and long options are no longer hidden from the model,
        // so they are counted here instead. Losing a backtick pair turns code into
        // prose in the rendered output, and a translated --flag is simply wrong.
        var srcTicks = Backtick.Matches(source).Count;
        var outTicks = Backtick.Matches(translated).Count;
        if (srcTicks != outTicks)
        {
            return $"backtick count changed: {srcTicks} in source, {outTicks} in output";
        }

        var options = CheckOptions(source, translated);
        if (options is not null)
        {
            return options;
        }

        // A URL the source did not have. "| Choice | What it fetches |" came back
        // with "https://www.cnet.com/news/" appended: no backtick moved, no
        // placeholder changed, the pipe count was restored by the rejoin, and
        // every gate passed. A translation cannot introduce a link, so counting
        // them catches the whole class of invented content that happens to be
        // structurally well-formed.
        var urlSrc = UrlScheme.Matches(source).Count;
        var urlOut = UrlScheme.Matches(translated).Count;
        if (urlOut != urlSrc)
        {
            return $"url count changed: {urlSrc} -> {urlOut}";
        }

        var brackets = CheckBrackets(source, translated, allowAddedBrackets);
        if (brackets is not null)
        {
            return brackets;
        }

        var terms = CheckDoNotTranslate(source, translated, doNotTranslate, declinable);
        if (terms is not null)
        {
            return terms;
        }

        var shape = CheckLineShape(source, translated);
        if (shape is not null)
        {
            return shape;
        }

        var srcLen = Whitespace.Replace(source, " ").Trim().Length;
        var outLen = Whitespace.Replace(translated, " ").Trim().Length;

        // PowerShellCast, not a cast: [int] in the reference rounds where (int)
        // here truncates, which moves the line between a chunk that is accepted
        // and one that is thrown away.
        if (srcLen >= 200 && outLen < Text.PowerShellCast.ToInt(srcLen * minLengthRatio))
        {
            var pct = Text.PowerShellCast.ToInt(100.0 * outLen / Math.Max(1, srcLen));
            return $"answer collapsed to {pct}% of the source length, content was probably dropped";
        }

        if (srcLen >= 40 && string.Equals(translated.Trim(), source.Trim(), StringComparison.Ordinal))
        {
            return "source returned unchanged";
        }

        return null;
    }

    private static string? CheckSentinels(string source, string translated)
    {
        var srcIds = MarkupGuard.SentinelIds(source);
        var outIds = MarkupGuard.SentinelIds(translated);

        if (srcIds.Count != outIds.Count)
        {
            var missing = srcIds.Where(id => !outIds.Contains(id)).ToList();
            var extra = outIds.Where(id => !srcIds.Contains(id)).ToList();

            var detail = new List<string>();
            if (missing.Count > 0)
            {
                detail.Add("missing " + string.Join(",", missing));
            }

            if (extra.Count > 0)
            {
                detail.Add("invented " + string.Join(",", extra));
            }

            return $"protected block count changed: expected {srcIds.Count}, got {outIds.Count} ({string.Join("; ", detail)})";
        }

        for (var i = 0; i < srcIds.Count; i++)
        {
            if (srcIds[i] != outIds[i])
            {
                return "protected block ids altered";
            }
        }

        return null;
    }

    private static string? CheckPlaceholders(string source, string translated)
    {
        var ps = MarkupGuard.Placeholders(source);
        var po = MarkupGuard.Placeholders(translated);

        if (ps.Count != po.Count)
        {
            return $"placeholder count changed: expected {ps.Count}, got {po.Count}";
        }

        for (var i = 0; i < ps.Count; i++)
        {
            if (!string.Equals(ps[i], po[i], StringComparison.Ordinal))
            {
                return $"placeholder altered: expected '{ps[i]}', got '{po[i]}'";
            }
        }

        return null;
    }

    private static string? CheckOptions(string source, string translated)
    {
        var srcOpts = LongOption.Matches(source).Select(m => m.Value).ToList();
        var outOpts = LongOption.Matches(translated).Select(m => m.Value).ToList();

        srcOpts.Sort(MarkupGuard.Order);
        outOpts.Sort(MarkupGuard.Order);

        if (srcOpts.Count != outOpts.Count)
        {
            return $"command option count changed: {srcOpts.Count} in source, {outOpts.Count} in output";
        }

        for (var i = 0; i < srcOpts.Count; i++)
        {
            if (!string.Equals(srcOpts[i], outOpts[i], StringComparison.Ordinal))
            {
                return $"command option altered: '{srcOpts[i]}' became '{outOpts[i]}'";
            }
        }

        return null;
    }

    /// <summary>
    /// Brackets, counted by side.
    ///
    /// Segmentation cuts a line at its markup, so a fragment can end mid-bracket:
    /// "Through the proxy (" was answered with a closing bracket instead, and the
    /// rejoined line carried two closers and no opener. Counting each side
    /// separately catches a flip that a total would not.
    ///
    /// Skipped when the SOURCE is itself unbalanced. Prose wraps, so a
    /// parenthetical routinely opens on one line and closes on the next; counting
    /// those per line reported six correct translations as damaged, because the
    /// model reasonably closed the bracket it was shown.
    /// </summary>
    private static string? CheckBrackets(string source, string translated, bool allowAdded)
    {
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']') })
        {
            var sOpen = Count(source, open);
            var sClose = Count(source, close);

            if (sOpen != sClose)
            {
                continue;
            }

            var tOpen = Count(translated, open);
            var tClose = Count(translated, close);

            // A clarification the translator added, and balanced. Nothing was
            // lost: the source had none of this bracket, so nothing of its can
            // have gone missing.
            if (allowAdded && sOpen == 0 && tOpen == tClose)
            {
                continue;
            }

            if (tOpen != sOpen || tClose != sClose)
            {
                return $"bracket count changed: '{open}' {sOpen}->{tOpen}, '{close}' {sClose}->{tClose}";
            }
        }

        return null;

        static int Count(string text, char c) => text.Count(ch => ch == c);
    }

    /// <summary>
    /// Do-not-translate, with an exception for inflection.
    ///
    /// An exact-count test fights the target language. Measured: "a future
    /// llmster release" came back as "budouci vydani LLMsteru", correct Czech in
    /// the locative case, and was refused -- so the sentence stayed English for
    /// no good reason. A trailing case ending was already tolerated because the
    /// term is a substring; what failed was the capitalisation. So a declinable
    /// term is matched case-insensitively and allowed up to three trailing
    /// letters. Terms that never inflect -- .NET, C#, JSON -- stay strict, where
    /// any change at all is damage. Which list a term is on is a config decision,
    /// not a guess made here.
    /// </summary>
    private static string? CheckDoNotTranslate(
        string source,
        string translated,
        IReadOnlyList<string> doNotTranslate,
        IReadOnlyList<string> declinable)
    {
        foreach (var term in doNotTranslate)
        {
            if (string.IsNullOrEmpty(term))
            {
                continue;
            }

            var isDeclinable = declinable.Contains(term, StringComparer.Ordinal);
            var inSrc = Regex.Matches(source, Regex.Escape(term)).Count;

            var inOut = isDeclinable
                ? Regex.Matches(translated, Regex.Escape(term) + @"\p{L}{0,3}", RegexOptions.IgnoreCase).Count
                : Regex.Matches(translated, Regex.Escape(term)).Count;

            if (inSrc > 0 && inOut < inSrc)
            {
                var how = isDeclinable ? " (inflected forms allowed)" : string.Empty;
                return $"do-not-translate term lost: '{term}' ({inSrc} -> {inOut}){how}";
            }
        }

        return null;
    }

    /// <summary>
    /// Line-shape checks, applied only when the source really is a single line.
    ///
    /// These close the gap that let a run reach 85% coverage while losing 24 of
    /// 43 headings and 19 of 124 table rows. Every per-line gate passed -- the
    /// words were fine, the backticks balanced -- because nothing checked that a
    /// heading was still a heading. Markdown carries meaning in the first
    /// characters of a line and in its pipe count; losing either silently
    /// reshapes the document.
    /// </summary>
    private static string? CheckLineShape(string source, string translated)
    {
        if (source.Contains('\n', StringComparison.Ordinal))
        {
            return null;
        }

        if (translated.Contains('\n', StringComparison.Ordinal))
        {
            return "answer spans multiple lines but the source was one line";
        }

        var srcMarker = Marker(source);
        var outMarker = Marker(translated);

        if (!string.Equals(srcMarker, outMarker, StringComparison.Ordinal))
        {
            return $"line prefix changed: '{srcMarker}' became '{outMarker}'";
        }

        var srcPipes = Pipe.Matches(source).Count;
        var outPipes = Pipe.Matches(translated).Count;

        if (srcPipes != outPipes)
        {
            return $"table cell count changed: {srcPipes} pipe(s) became {outPipes}";
        }

        return null;

        static string Marker(string line)
        {
            var m = LineMarker.Match(line);
            return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
        }
    }

    /// <summary>
    /// Strips a translator's note before the answer is judged.
    ///
    /// Asked for a heading, the model returned the correct Czech followed by
    /// "(prelozeno z anglictiny)". The leak gate caught it and the line was
    /// refused, so a good translation was thrown away over a trailing annotation.
    ///
    /// This is a repair, not a relaxation: the note is removed and the cleaned
    /// answer still faces every gate. Only a trailing parenthetical that talks
    /// about translating, and that the source does not itself have, is touched.
    /// </summary>
    public static string RemoveTranslatorNote(string source, string? translated)
    {
        ArgumentNullException.ThrowIfNull(source);

        var t = translated ?? string.Empty;

        if (NoteRx.IsMatch(t) && !NoteRx.IsMatch(source))
        {
            t = NoteRx.Replace(t, string.Empty);
        }

        // An ellipsis standing in for text the model did not translate.
        if (EllipsisRx.IsMatch(t) && !SourceEllipsisRx.IsMatch(source))
        {
            t = EllipsisRx.Replace(t, string.Empty);
        }

        return t;
    }

    private static readonly Regex NoteRx = new(
        @"(?i)\s*[\(\[][^)\]]*(p[řr]elo[žz]|p[řr]eklad|translat|angli[čc]tin|english|[čc]e[šs]tin)[^)\]]*[\)\]]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex EllipsisRx = new(@"\s*\.\.\.\s*$", RegexOptions.Compiled);

    private static readonly Regex SourceEllipsisRx = new(@"\.\.\.\s*$", RegexOptions.Compiled);
}
