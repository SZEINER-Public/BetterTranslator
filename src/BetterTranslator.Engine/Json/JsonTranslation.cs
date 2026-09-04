using System.Text;
using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Json;

/// <summary>
/// Translates a JSON document without ever asking the model for JSON.
///
/// The model is shown text and returns text. Every brace, bracket, comma, quote
/// and space in the output comes from the input, because the output *is* the
/// input with a few byte ranges replaced. Keys are not sent at all -- not
/// protected, not sent -- so no answer can rename one, and key order is
/// preserved for the same reason it is in any file nobody rewrote.
/// </summary>
public static partial class JsonTranslation
{
    /// <summary>
    /// How many values go in one request, and the ceiling on sentinels within
    /// it. The count is bounded by both: a batch of short labels can carry
    /// eight, one of placeholder-heavy sentences hits the sentinel ceiling
    /// first. The ceiling is the figure measured in
    /// <see cref="Markup.MarkupGuard"/> -- past it a small model stops echoing
    /// tokens in order and every answer is refused.
    /// </summary>
    private const int MaxValuesPerBatch = 8;

    private const int MaxSentinelsPerBatch = 14;

    [GeneratedRegex(@"\[\[(\d+)\]\]")]
    private static partial Regex SentinelPattern { get; }

    /// <param name="translate">
    /// Sends one request and returns what came back. Called in the caller's
    /// context: this method does no work of its own between calls, and taking a
    /// UI caller's callback off its thread would be a decision it has no
    /// business making.
    /// </param>
    public static async Task<JsonTranslationResult> TranslateAsync(
        string json,
        Func<string, CancellationToken, Task<string?>> translate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(translate);

        var scalars = JsonSegmenter.Segment(json);

        if (scalars is null)
        {
            // Not JSON. The caller should not have routed it here, and returning
            // it untouched is the only safe answer.
            return new JsonTranslationResult(json, 0, 0, []);
        }

        var wanted = scalars
            .Where(s => s.IsString && JsonValueGuard.WorthSending(s.Text))
            .ToList();

        if (wanted.Count == 0)
        {
            return new JsonTranslationResult(json, 0, 0, []);
        }

        var literalNonAscii = JsonSegmenter.PrefersLiteralNonAscii(json);
        var answers = new Dictionary<int, string>();
        var raw = new Dictionary<int, (string Answer, IReadOnlyList<JsonGuard> Guards)>();

        var stopped = false;

        foreach (var batch in Batches(wanted))
        {
            if (await RunAsync(batch, translate, answers, raw, cancellationToken).ConfigureAwait(false))
            {
                stopped = true;
                break;
            }
        }

        var kept = wanted.Where(s => !answers.ContainsKey(s.Start)).ToList();

        var text = Splice(
            json,
            wanted.Where(s => answers.ContainsKey(s.Start))
                  .Select(s => (s.Start, s.Length, JsonEscape.Content(answers[s.Start], literalNonAscii)))
                  .ToList());

        return new JsonTranslationResult(
            text,
            answers.Count,
            kept.Count,
            [.. kept.Select(s => s.KeyPath)],
            stopped)
        {
            Segments = Segments(json, text, wanted, answers, raw, literalNonAscii),
        };
    }

    private static IReadOnlyList<Verification.Structure.SegmentTrace> Segments(
        string json,
        string text,
        List<JsonScalar> wanted,
        Dictionary<int, string> answers,
        Dictionary<int, (string Answer, IReadOnlyList<JsonGuard> Guards)> raw,
        bool literalNonAscii)
    {
        var sourceOffsets = Text.Utf8Offsets.Of(json);
        var targetOffsets = Text.Utf8Offsets.Of(text);
        var traces = new List<Verification.Structure.SegmentTrace>();
        var delta = 0;

        foreach (var scalar in wanted.OrderBy(s => s.Start))
        {
            var (sourceStart, sourceLength) = sourceOffsets.CharRange(scalar.Start, scalar.Length);
            var targetByteStart = scalar.Start + delta;

            if (answers.TryGetValue(scalar.Start, out var answer))
            {
                var escaped = JsonEscape.Content(answer, literalNonAscii);
                var escapedBytes = Encoding.UTF8.GetByteCount(escaped);
                var (targetStart, targetLength) = targetOffsets.CharRange(targetByteStart, escapedBytes);
                var (rawAnswer, guards) = raw.TryGetValue(scalar.Start, out var recorded) ? recorded : (answer, []);

                traces.Add(new Verification.Structure.SegmentTrace(
                    sourceStart,
                    sourceLength,
                    Core.Verification.Checks.SegmentOutcome.Translated,
                    rawAnswer,
                    [.. guards.Select(g => new Verification.Structure.MaskTrace(
                        g.Sentinel,
                        JsonEscape.Content(g.Original, literalNonAscii),
                        Verification.Structure.SegmentTrace.ReasonFor(g.Original)))],
                    escaped,
                    targetStart,
                    targetLength));

                delta += escapedBytes - scalar.Length;
            }
            else
            {
                var (targetStart, targetLength) = targetOffsets.CharRange(targetByteStart, scalar.Length);

                traces.Add(new Verification.Structure.SegmentTrace(
                    sourceStart,
                    sourceLength,
                    Core.Verification.Checks.SegmentOutcome.Kept,
                    TargetStart: targetStart,
                    TargetLength: targetLength));
            }
        }

        return traces;
    }

    /// <summary>
    /// Groups values into requests. Bounded by count and by sentinels, so a
    /// batch is never so large that the model loses track of it.
    /// </summary>
    private static List<List<JsonScalar>> Batches(List<JsonScalar> wanted)
    {
        var batches = new List<List<JsonScalar>>();
        var batch = new List<JsonScalar>();
        var sentinels = 0;

        foreach (var scalar in wanted)
        {
            var (_, guards) = JsonValueGuard.Protect(scalar.Text);

            // One marker for the value itself, plus one per piece of machinery
            // inside it.
            var cost = 1 + guards.Count;

            if (batch.Count > 0 && (batch.Count >= MaxValuesPerBatch || sentinels + cost > MaxSentinelsPerBatch))
            {
                batches.Add(batch);
                batch = [];
                sentinels = 0;
            }

            batch.Add(scalar);
            sentinels += cost;
        }

        if (batch.Count > 0)
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>
    /// One request for a batch, and one request per value if the batch comes
    /// back wrong. A value that fails both keeps its source: the document stays
    /// valid JSON and one entry stays in the source language, which is a state
    /// the reader can see and act on.
    /// </summary>
    private static async Task<bool> RunAsync(
        List<JsonScalar> batch,
        Func<string, CancellationToken, Task<string?>> translate,
        Dictionary<int, string> answers,
        Dictionary<int, (string Answer, IReadOnlyList<JsonGuard> Guards)> raw,
        CancellationToken cancellationToken)
    {
        if (batch.Count > 1)
        {
            var request = Build(batch, out var markers, out var guards);
            var (answer, halted) = await Documents.Stoppable.UnitAsync(translate, request, cancellationToken)
                .ConfigureAwait(false);

            if (halted)
            {
                return true;
            }

            if (TrySplit(answer, markers, guards, batch, answers, raw))
            {
                return false;
            }
        }

        foreach (var scalar in batch)
        {
            var (protectedText, guards) = JsonValueGuard.Protect(scalar.Text);
            var (answer, halted) = await Documents.Stoppable.UnitAsync(translate, protectedText, cancellationToken)
                .ConfigureAwait(false);

            if (halted)
            {
                return true;
            }

            if (JsonValueGuard.Holds(answer, guards))
            {
                answers[scalar.Start] = JsonValueGuard.Restore(answer!.Trim(), guards);
                raw[scalar.Start] = (answer!.Trim(), guards);
            }
        }

        return false;
    }

    /// <summary>
    /// The request. One value per line, each behind its own marker.
    ///
    /// Markers and the machinery inside the values are numbered from one
    /// sequence, so a slot in the third value can never be mistaken for the
    /// marker of the fourth.
    /// </summary>
    private static string Build(
        List<JsonScalar> batch,
        out List<string> markers,
        out List<IReadOnlyList<JsonGuard>> guards)
    {
        markers = [];
        guards = [];

        var next = batch.Count;
        var request = new StringBuilder();

        for (var i = 0; i < batch.Count; i++)
        {
            var (protectedText, valueGuards) = JsonValueGuard.Protect(batch[i].Text, next);
            next += valueGuards.Count;

            var marker = $"[[{i}]]";
            markers.Add(marker);
            guards.Add(valueGuards);

            request.Append(marker).Append(' ').Append(protectedText);

            if (i < batch.Count - 1)
            {
                request.Append('\n');
            }
        }

        return request.ToString();
    }

    /// <summary>
    /// Splits an answer back into its values.
    ///
    /// Everything is mapped by marker, and each value is written to the span it
    /// came from rather than to its key. Two keys holding the same English word
    /// therefore get their own answers written to their own places, and a
    /// document whose keys are in any order at all is no different from one in
    /// alphabetical order.
    /// </summary>
    private static bool TrySplit(
        string? answer,
        List<string> markers,
        List<IReadOnlyList<JsonGuard>> guards,
        List<JsonScalar> batch,
        Dictionary<int, string> answers,
        Dictionary<int, (string Answer, IReadOnlyList<JsonGuard> Guards)> raw)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var found = SentinelPattern.Matches(answer);
        var positions = new int[markers.Count];

        // Every marker exactly once, in order. A missing one means a value was
        // dropped; a reordered pair means two translations would be written to
        // each other's keys.
        var seen = 0;

        foreach (Match match in found)
        {
            if (seen < markers.Count && match.Value == markers[seen])
            {
                positions[seen] = match.Index;
                seen++;
            }
        }

        if (seen != markers.Count)
        {
            return false;
        }

        var staged = new Dictionary<int, string>();
        var stagedRaw = new Dictionary<int, (string Answer, IReadOnlyList<JsonGuard> Guards)>();

        for (var i = 0; i < markers.Count; i++)
        {
            var from = positions[i] + markers[i].Length;
            var to = i + 1 < markers.Count ? positions[i + 1] : answer.Length;

            var value = answer[from..to].Trim();

            if (value.Length == 0 || !JsonValueGuard.Holds(value, guards[i]))
            {
                return false;
            }

            staged[batch[i].Start] = JsonValueGuard.Restore(value, guards[i]);
            stagedRaw[batch[i].Start] = (value, guards[i]);
        }

        // Written only once the whole batch has validated, so a half-good answer
        // does not leave half the values translated and half not.
        foreach (var (start, value) in staged)
        {
            answers[start] = value;
            raw[start] = stagedRaw[start];
        }

        return true;
    }

    /// <summary>
    /// Replaces the byte ranges the translated values occupied, back to front so
    /// an earlier edit cannot move the offsets of one not yet applied.
    /// </summary>
    private static string Splice(string json, List<(int Start, int Length, string Text)> edits)
    {
        if (edits.Count == 0)
        {
            return json;
        }

        edits.Sort((a, b) => b.Start.CompareTo(a.Start));

        var bytes = new List<byte>(Encoding.UTF8.GetBytes(json));

        foreach (var (start, length, text) in edits)
        {
            bytes.RemoveRange(start, length);
            bytes.InsertRange(start, Encoding.UTF8.GetBytes(text));
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}
