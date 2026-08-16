using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Markup;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// What one unit came back as. Carries the refusal rather than only the text,
/// because a unit that kept its source looks identical to one nothing was sent
/// for.
/// </summary>
public sealed record UnitOutcome(string? Text, int GeneratedTokens, string? Refusal);

/// <summary>What the document pass is doing right now.</summary>
public sealed record DocumentProgress
{
    public required string Activity { get; init; }

    public int Pass { get; init; }

    public int UnitsDone { get; init; }

    public int UnitsTotal { get; init; }

    public int LinesTranslated { get; init; }

    public int LinesTotal { get; init; }

    public bool IsFinished { get; init; }

    public bool WasCancelled { get; init; }

    public double Fraction => UnitsTotal <= 0 ? 0 : Math.Clamp((double)UnitsDone / UnitsTotal, 0, 1);

    public int Percent => (int)Math.Floor(Fraction * 100);
}

/// <summary>
/// A translated document and what it cost. The line count is part of the
/// contract, not a statistic: <see cref="LinesTotal"/> always equals the source's.
/// </summary>
public sealed record DocumentTranslation
{
    public required string Text { get; init; }

    public required int LinesTotal { get; init; }

    public int LinesTranslated { get; init; }

    /// <summary>Lines that kept their source, because nothing acceptable came back.</summary>
    public int LinesKept { get; init; }

    /// <summary>Lines that were never candidates -- code, markup, blanks.</summary>
    public int LinesUntouched { get; init; }

    public int ParagraphsJoined { get; init; }

    public int SpacesRepaired { get; init; }

    /// <summary>Fragments replaced in place inside an otherwise good line.</summary>
    public int FragmentsRepaired { get; init; }

    /// <summary>Lines the finishing pass gave up patching and translated whole.</summary>
    public int LinesRetranslated { get; init; }

    /// <summary>Distinct fragments a model was asked to judge.</summary>
    public int DefectsAdjudicated { get; init; }

    public int GeneratedTokens { get; init; }

    public TimeSpan Duration { get; init; }

    public bool WasCancelled { get; init; }

    /// <summary>
    /// Why lines were refused, counted per reason and carrying no text. A
    /// document is payload; a report that quoted it would become a listing of it.
    /// </summary>
    public IReadOnlyList<string> Refusals { get; init; } = [];
}

/// <summary>
/// Translates a whole document with its line count intact. The orchestration
/// half of `translate-master.ps1`.
///
/// Two properties are the whole point, and they pull against each other. The
/// model needs a SENTENCE to translate -- handed one wrapped line at a time it
/// renders each fragment on its own and the result reads as fragments. The
/// document needs its LINE COUNT back -- a file whose line count changed cannot
/// be written over the original, and a diff of it is unreadable. Grouping
/// consecutive prose lines into a paragraph, translating that, and wrapping the
/// answer back to exactly the lines it came from is what satisfies both.
///
/// One work item per UNIT, where a unit is a whole wrapped paragraph if there is
/// one and a single line otherwise. The joining happens here rather than inside
/// the translator, so the translator still receives one piece of text and returns
/// one piece of text and every gate it runs is unchanged.
/// </summary>
public sealed class DocumentTranslator(
    Func<TranslationJob, CancellationToken, Task<UnitOutcome>> translate)
{
    /// <summary>
    /// How many times a still-untranslated line is offered again.
    ///
    /// A model handed the same prompt twice does not answer the same way twice,
    /// so a line refused on one pass often passes on the next. Two is the
    /// reference's count and the same reasoning as the single-line retry: a third
    /// attempt on text that has already produced damage twice produces it again.
    /// </summary>
    public int MaxPasses { get; init; } = 2;

    /// <summary>
    /// Asks the model to judge a defect no rule can settle -- see
    /// <see cref="FinalRepairPass"/>. Null, the default, still runs the final
    /// pass but acts only on the certain verdicts: the ambiguous ones fall to
    /// KEEP, which changes nothing.
    ///
    /// Optional because it costs a model call per distinct fragment and answers a
    /// question, not a translation. Whether that is worth paying for is the
    /// caller's decision, not this class's.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string?>>? Adjudicate { get; init; }

    /// <summary>
    /// How many letters a line needs before it is worth sending.
    ///
    /// The default is the document floor, which keeps near-empty lines and bare
    /// markup away from the model. A composer send wants it lower: every line
    /// somebody typed into the box is meant to be translated, and "Thanks."
    /// silently staying English because it is six letters long reads as the app
    /// having missed it.
    /// </summary>
    public int MinLetters { get; init; } = Engine.Slop.TranslationCandidate.DefaultMinLetters;

    /// <summary>
    /// A blockquote marker on the first line of a group. It belongs to every line
    /// of the quote, not to the paragraph, so it is stripped before the join and
    /// re-applied to each wrapped line -- a paragraph that lost it from line two
    /// onward stops being a blockquote halfway through.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex QuoteMarker = new(@"^\s*>\s?");

    public async Task<DocumentTranslation> TranslateAsync(
        TranslationJob job,
        IProgress<DocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Line endings are normalised once and restored once, so every index in
        // between refers to the same thing.
        var newline = job.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var source = job.Text.ReplaceLineEndings("\n").Split('\n');
        var lines = (string[])source.Clone();

        var terms = Engine.Slop.DoNotTranslate.Load();
        var refusals = new List<string>();
        var translated = 0;
        var joined = 0;
        var tokens = 0;
        var cancelled = false;

        // Candidates are recomputed each pass rather than carried, so a line that
        // was translated on pass one is not offered again on pass two.
        for (var pass = 1; pass <= MaxPasses && !cancelled; pass++)
        {
            var candidates = ParagraphSegmenter
                .Candidates(source, terms.Strict, MinLetters)
                .Where(i => string.Equals(lines[i], source[i], StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                break;
            }

            var units = Units(source, candidates);
            joined += units.Count(u => u.Indices.Count > 1);

            var done = 0;

            progress?.Report(new DocumentProgress
            {
                Activity = $"Pass {pass} - {units.Count} unit{(units.Count == 1 ? "" : "s")}",
                Pass = pass,
                UnitsTotal = units.Count,
                LinesTranslated = translated,
                LinesTotal = source.Length,
            });

            foreach (var unit in units)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                UnitOutcome outcome;

                try
                {
                    outcome = await translate(job with { Text = unit.Text }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }

                tokens += outcome.GeneratedTokens;
                done++;

                var placed = Place(lines, unit, outcome, refusals);
                translated += placed;

                progress?.Report(new DocumentProgress
                {
                    Activity = $"Pass {pass} - line {unit.Indices[0] + 1} of {source.Length}",
                    Pass = pass,
                    UnitsDone = done,
                    UnitsTotal = units.Count,
                    LinesTranslated = translated,
                    LinesTotal = source.Length,
                });
            }
        }

        // The finishing pass. Skipped after a cancel: repairing a document whose
        // translation was interrupted spends model calls on lines the operator
        // has already stopped caring about.
        var tally = new RepairTally();

        if (!cancelled)
        {
            progress?.Report(new DocumentProgress
            {
                Activity = "Checking what came back",
                LinesTranslated = translated,
                LinesTotal = source.Length,
            });

            try
            {
                tally = await new FinalRepairPass(translate, Adjudicate ?? NeverAsk)
                    .RunAsync(job, source, lines, terms.Strict, cancellationToken)
                    .ConfigureAwait(false);

                tokens += tally.GeneratedTokens;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }

        progress?.Report(new DocumentProgress
        {
            Activity = cancelled ? "Stopped" : "Done",
            LinesTranslated = translated,
            LinesTotal = source.Length,
            IsFinished = true,
            WasCancelled = cancelled,
        });

        var candidateCount = ParagraphSegmenter.Candidates(source, terms.Strict, MinLetters).Count;

        return new DocumentTranslation
        {
            Text = string.Join(newline, lines),
            LinesTotal = source.Length,
            LinesTranslated = translated,
            LinesKept = candidateCount - translated,
            LinesUntouched = source.Length - candidateCount,
            ParagraphsJoined = joined,
            SpacesRepaired = tally.SpacesInserted,
            FragmentsRepaired = tally.FragmentsRepaired,
            LinesRetranslated = tally.LinesRetranslated,
            DefectsAdjudicated = tally.Adjudicated,
            GeneratedTokens = tokens,
            Duration = clock.Elapsed,
            WasCancelled = cancelled,
            Refusals = Summarise(refusals),
        };
    }

    /// <summary>One thing to send: a paragraph, or a line that stands alone.</summary>
    private sealed record Unit(IReadOnlyList<int> Indices, string Text, string Prefix);

    /// <summary>
    /// Groups the candidates into units. Paragraph first, then every candidate
    /// that no paragraph claimed, in document order so a partial run reads as
    /// having got part-way down the file rather than having skipped about.
    /// </summary>
    private static List<Unit> Units(IReadOnlyList<string> lines, IReadOnlyList<int> candidates)
    {
        var groups = ParagraphSegmenter.Paragraphs(lines, candidates);
        var claimed = new HashSet<int>(groups.SelectMany(g => g));
        var units = new List<Unit>();

        foreach (var group in groups)
        {
            var marker = QuoteMarker.Match(lines[group[0]]);
            var prefix = marker.Success ? marker.Value : string.Empty;

            var text = string.Join(' ', group.Select(i => QuoteMarker.Replace(lines[i], string.Empty))).Trim();

            units.Add(new Unit(group, text, prefix));
        }

        foreach (var index in candidates.Where(i => !claimed.Contains(i)))
        {
            units.Add(new Unit([index], lines[index], string.Empty));
        }

        return [.. units.OrderBy(u => u.Indices[0])];
    }

    /// <summary>
    /// Writes one answer onto the lines it came from, and returns how many landed.
    ///
    /// A paragraph comes back as one piece of text and has to land on exactly the
    /// lines it came from. If it cannot be wrapped to that many it is REFUSED
    /// rather than written short: a paragraph that lost a line would fail the
    /// line-count check that proves nothing was dropped, and padding it with a
    /// blank would split the paragraph in two when the Markdown is rendered.
    /// </summary>
    private static int Place(string[] lines, Unit unit, UnitOutcome outcome, List<string> refusals)
    {
        if (string.IsNullOrWhiteSpace(outcome.Text))
        {
            refusals.Add(outcome.Refusal ?? "no answer");
            return 0;
        }

        if (unit.Indices.Count == 1)
        {
            lines[unit.Indices[0]] = outcome.Text!;
            return 1;
        }

        var wrapped = LineSplitter.Split(outcome.Text!, unit.Indices.Count, unit.Prefix);

        if (wrapped is null || wrapped.Count != unit.Indices.Count)
        {
            refusals.Add("unwrappable: fewer words than the paragraph had lines");
            return 0;
        }

        for (var n = 0; n < unit.Indices.Count; n++)
        {
            lines[unit.Indices[n]] = wrapped[n];
        }

        return unit.Indices.Count;
    }

    /// <summary>
    /// The adjudicator used when the caller supplied none: it answers nothing,
    /// so every ambiguous defect falls to KEEP and changes nothing. The certain
    /// verdicts still act, which is what makes the finishing pass worth running
    /// with no model behind it at all.
    /// </summary>
    private static Task<string?> NeverAsk(string instruction, string question, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// One line per distinct reason, with its count and its numbers masked. The
    /// document being translated is payload, and a report that quoted it would
    /// become a listing of it.
    /// </summary>
    private static IReadOnlyList<string> Summarise(IEnumerable<string> reasons) =>
    [
        .. reasons
            .Select(Engine.Memory.MemoryAudit.Mask)
            .GroupBy(r => r, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} x {g.Key}"),
    ];
}
