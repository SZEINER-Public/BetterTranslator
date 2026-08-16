using System.Text;
using BetterTranslator.Engine.Markup;

namespace BetterTranslator.Engine.Markdown;

/// <summary>
/// Translates a Markdown document without ever rendering one.
///
/// The output is the input string with translated text spliced over the spans
/// the prose occupied. Nothing is re-emitted from a syntax tree, so structure is
/// not preserved by a renderer being careful -- it is preserved because the
/// bytes around the prose are never touched. With translation switched off the
/// result is the input, character for character, and that is a property of the
/// method rather than a test that happens to pass.
/// </summary>
public static class MarkdownTranslation
{
    /// <summary>
    /// Past this many sentinels a unit goes straight to the run-by-run path
    /// without spending a call first.
    ///
    /// The figure is the lesson recorded in <see cref="MarkupGuard"/>: a 9B model
    /// could not echo fifty opaque tokens in order, so every chunk failed and
    /// nothing was translated. A block dense enough to need more than this is
    /// mostly markup, and its runs are short enough to translate well alone.
    /// </summary>
    private const int MaxSentinels = 12;

    /// <summary>
    /// Runs one document.
    /// </summary>
    /// <param name="translate">
    /// Sends one string to the model and returns what came back, or null when
    /// nothing did. It is called once per block on the normal path.
    ///
    /// Called in the caller's context, deliberately. The awaits below do not
    /// ConfigureAwait(false), which is the usual advice for a library, because
    /// this method does no work of its own between the calls -- it is a loop
    /// around somebody else's delegate. Dropping the context would hand a UI
    /// caller's callback to a thread pool thread, and a view model that raises a
    /// command's CanExecute from there takes the window down. It did.
    /// </param>
    public static async Task<MarkdownTranslationResult> TranslateAsync(
        string markdown,
        Func<string, CancellationToken, Task<string?>> translate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(translate);

        var units = MarkdownSegmenter.Segment(markdown);

        if (units.Count == 0)
        {
            return new MarkdownTranslationResult(markdown, 0, 0, 0, []);
        }

        var edits = new List<(int Start, int Length, string Text)>();
        int translated = 0, recovered = 0, kept = 0, unverified = 0;

        var answers = new List<(MarkdownUnit Unit, string? Answer, bool Holds, bool Echoed)>();

        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var answer = unit.Guards.Count <= MaxSentinels
                ? await translate(unit.Text, cancellationToken)
                : null;

            answers.Add((
                unit,
                answer,
                MarkdownEcho.Holds(unit, answer),
                UnitFidelity.Echoed(unit.Text, answer)));
        }

        // An echo is only a failure where translation was actually happening. A
        // run where every unit came back as itself is translation switched off,
        // and the document is meant to come back byte for byte; a run where one
        // block echoed while the rest translated is that block left in the source
        // language, which is the defect worth spending another call on.
        var working = Config.PipelineOptions.RecoverEchoedUnits
            && answers.Any(candidate => candidate.Holds && !candidate.Echoed);

        foreach (var (unit, answer, holds, echoed) in answers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (holds && !(echoed && working))
            {
                edits.Add((
                    unit.Start,
                    unit.Length,
                    RunBoundary.Preserve(unit.Text, MarkdownEcho.Restore(answer!, unit.Guards))));

                translated++;

                if (Config.PipelineOptions.FlagUnverifiedUnits && UnitFidelity.Unverified(unit.Text, answer))
                {
                    unverified++;
                }

                continue;
            }

            // The answer dropped, reordered or invented a sentinel, folded a line,
            // echoed its input, or the block was too dense to try. Each run of
            // prose goes on its own: a shorter span alone often translates where
            // the whole block came back untouched, and it cannot lose a bracket.
            var before = edits.Count;
            unverified += await RunsAsync(unit, markdown, translate, edits, cancellationToken);

            if (edits.Count > before)
            {
                recovered++;
            }
            else
            {
                kept++;
            }
        }

        var spliced = Splice(markdown, edits);
        var text = Markup.ByteHygiene.TrimIntroducedTrailingBlanks(
            markdown,
            EmphasisHygiene.RestoreAgainst(markdown, spliced));

        // The backstop. Per-unit checks cannot see a fence closed by an answer
        // two blocks away, and this is the check that would have caught exactly
        // that on the run these guards were written for.
        var issues = DocumentStructure.Compare(markdown, text);
        var lines = LineParity.Compare(markdown, text);

        if (lines is not null)
        {
            issues = [.. issues, lines];
        }

        return issues.Count == 0
            ? new MarkdownTranslationResult(text, translated, recovered, kept, issues, unverified)
            : new MarkdownTranslationResult(markdown, 0, 0, units.Count, issues, unverified);
    }

    private static async Task<int> RunsAsync(
        MarkdownUnit unit,
        string markdown,
        Func<string, CancellationToken, Task<string?>> translate,
        List<(int Start, int Length, string Text)> edits,
        CancellationToken cancellationToken)
    {
        var unverified = 0;

        foreach (var run in unit.Runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The span's edges are the space between it and the markup either
            // side. They are never sent and never replaced, so an answer cannot
            // weld the words onto that markup by returning them trimmed.
            var (start, length) = RunBoundary.Inner(markdown, run.Start, run.Length);
            var source = markdown.Substring(start, length);

            // Whitespace and punctuation between two marked-up words carry no
            // language. Sending them wastes a call and invites an answer that is
            // longer than what it replaces.
            if (!source.Any(char.IsLetter))
            {
                continue;
            }

            var answer = await translate(source, cancellationToken);

            if (UnitFidelity.Echoed(source, answer))
            {
                continue;
            }

            if (MarkdownEcho.RunHolds(answer))
            {
                // The span's own edges decide the spacing, in both directions: an
                // answer may not drop them and may not add its own either.
                edits.Add((start, length, RunBoundary.Preserve(source, answer!)));

                if (Config.PipelineOptions.FlagUnverifiedUnits && UnitFidelity.Unverified(source, answer))
                {
                    unverified++;
                }
            }
        }

        return unverified;
    }

    /// <summary>
    /// Applies the edits back to front, so an earlier edit cannot move the
    /// offsets of one that has not been applied yet.
    /// </summary>
    private static string Splice(string markdown, List<(int Start, int Length, string Text)> edits)
    {
        if (edits.Count == 0)
        {
            return markdown;
        }

        edits.Sort((a, b) => b.Start.CompareTo(a.Start));

        var text = new StringBuilder(markdown);

        foreach (var (start, length, replacement) in edits)
        {
            text.Remove(start, length).Insert(start, replacement);
        }

        return text.ToString();
    }
}
