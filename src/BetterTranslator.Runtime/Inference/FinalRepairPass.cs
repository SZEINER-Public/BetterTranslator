using System.Text.RegularExpressions;
using BetterTranslator.Engine.Markup;

namespace BetterTranslator.Runtime.Inference;

/// <summary>What the final pass changed. Counts only; the document is payload.</summary>
public sealed record RepairTally
{
    /// <summary>Distinct fragments a model was asked to judge.</summary>
    public int Adjudicated { get; init; }

    public int SpacesInserted { get; init; }

    /// <summary>Fragments replaced in place inside an otherwise good line.</summary>
    public int FragmentsRepaired { get; init; }

    /// <summary>Lines the pass gave up patching and translated whole.</summary>
    public int LinesRetranslated { get; init; }

    public int GeneratedTokens { get; init; }

    public bool ChangedNothing =>
        SpacesInserted == 0 && FragmentsRepaired == 0 && LinesRetranslated == 0;
}

/// <summary>
/// The pass that finishes the job. The orchestration half of
/// `translate-final.ps1`.
///
/// A document out of the translate passes is not yet done: some lines came back
/// with a clause still in the source language, and some came back with a word
/// welded to a protected term. Neither is visible to the gates that ran on them
/// -- a line with three English words in it loses no markup, invents nothing and
/// is the right length.
///
/// <see cref="TranslationDefects"/> grades what it finds. The certain ones are
/// acted on directly. The ambiguous ones are the reason
/// <see cref="DefectAdjudication"/> exists: "original assets" and "Web Audio" are
/// both two English words surviving in Czech output, and no mechanical test
/// separates them -- one is prose that should have been translated, the other is
/// a technical term that correctly stays. That judgement needs meaning, so it is
/// the one place in this engine where a model is asked a QUESTION rather than
/// given a translation job.
/// </summary>
public sealed class FinalRepairPass(
    Func<TranslationJob, CancellationToken, Task<UnitOutcome>> translate,
    Func<string, string, CancellationToken, Task<string?>> ask)
{
    /// <summary>
    /// Above this share of a line's prose still being source, the line is
    /// translated whole rather than patched.
    ///
    /// A line that is mostly source is not a line with residue in it -- it is a
    /// line the engine gave up on entirely, and patching fragments into it is the
    /// wrong repair. Measured: "On Windows you can also use `build-biotank.bat`
    /// to cross-build both binaries into `dist/`." came back untouched, and the
    /// fragment repair produced Czech for "you can also use" and for "to
    /// cross-build both binaries into" while leaving "On Windows" English between
    /// them. A gate then refused the mixture, correctly, and the line stayed as it
    /// was -- two model calls spent for nothing.
    /// </summary>
    public const double WholeLineShare = 0.6;

    private static readonly Regex ProseWord = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private readonly DefectAdjudication _adjudicator = new();

    /// <summary>
    /// Repairs <paramref name="lines"/> in place and reports what it changed.
    /// Only lines that were actually translated are examined: an untouched line
    /// is source by definition, and every word of it would read as residue.
    /// </summary>
    public async Task<RepairTally> RunAsync(
        TranslationJob job,
        IReadOnlyList<string> source,
        string[] lines,
        IReadOnlyList<string> doNotTranslate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lines);

        Core.Verification.Checks.CheckInstrumentation.Hit("repair/run");
        var adjudicated = 0;
        var spaces = 0;
        var fragments = 0;
        var whole = 0;
        var tokens = 0;

        for (var i = 0; i < lines.Length && i < source.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(lines[i], source[i], StringComparison.Ordinal))
            {
                continue;
            }

            var defects = TranslationDefects.Find(source[i], lines[i], doNotTranslate: doNotTranslate);

            // Ambiguous items are settled BEFORE anything is repaired, so the
            // decision is made once per fragment and the repair below works from
            // a resolved list rather than re-asking mid-edit.
            var actions = new List<(AdjudicatedVerdict Verdict, TranslationDefect Defect)>();

            foreach (var defect in defects)
            {
                if (defect.Verdict == DefectVerdict.CertainOk)
                {
                    continue;
                }

                if (defect.Verdict == DefectVerdict.CertainBad)
                {
                    // Glue is a spacing fault; residue is a translation fault.
                    actions.Add((
                        defect.Kind == DefectKind.Glue ? AdjudicatedVerdict.Space : AdjudicatedVerdict.Translate,
                        defect));

                    continue;
                }

                var before = _adjudicator.Asked;

                var verdict = await _adjudicator
                    .AdjudicateAsync(defect, source[i], lines[i], job.From.Name, job.To.Name, ask, cancellationToken)
                    .ConfigureAwait(false);

                if (_adjudicator.Asked > before)
                {
                    adjudicated++;
                }

                if (verdict != AdjudicatedVerdict.Keep)
                {
                    actions.Add((verdict, defect));
                }
            }

            if (actions.Count == 0)
            {
                continue;
            }

            // Spacing first, and by script alone. A missing separator needs no
            // translation -- the words on both sides are already correct -- so
            // inserting it is a text operation, not a model call. Done before any
            // translation so a repaired line is measured, and gated, in its final
            // shape. It changes nothing but whitespace, so it cannot fail a
            // structural gate.
            foreach (var (_, defect) in actions.Where(a => a.Verdict == AdjudicatedVerdict.Space))
            {
                if (InsertSpace(lines, i, defect.Text))
                {
                    spaces++;
                    Core.Verification.Checks.CheckInstrumentation.Hit("repair/space-inserted");
                }
            }

            var runs = actions
                .Where(a => a.Verdict == AdjudicatedVerdict.Translate)
                .Select(a => a.Defect)
                .ToList();

            if (runs.Count == 0)
            {
                continue;
            }

            var prose = ProseWord.Matches(source[i]).Count;
            var residue = runs.Sum(r => r.Words);

            if (prose > 0 && (double)residue / prose >= WholeLineShare)
            {
                var outcome = await translate(job with { Text = source[i] }, cancellationToken).ConfigureAwait(false);
                tokens += outcome.GeneratedTokens;

                if (!string.IsNullOrWhiteSpace(outcome.Text))
                {
                    lines[i] = outcome.Text!;
                    whole++;
                    Core.Verification.Checks.CheckInstrumentation.Hit("repair/line-retranslated");
                }

                continue;
            }

            foreach (var run in runs)
            {
                var pattern = new Regex(
                    @"(?<![\p{L}\p{Nd}])" + Regex.Escape(run.Text) + @"(?![\p{L}\p{Nd}])");

                if (!pattern.IsMatch(lines[i]))
                {
                    continue;
                }

                var outcome = await translate(job with { Text = run.Text }, cancellationToken).ConfigureAwait(false);
                tokens += outcome.GeneratedTokens;

                if (string.IsNullOrWhiteSpace(outcome.Text))
                {
                    continue;
                }

                // Only the first occurrence. A repeated phrase is handled by the
                // next run of this pass, which re-detects what is left.
                lines[i] = pattern.Replace(lines[i], outcome.Text!.Replace("$", "$$", StringComparison.Ordinal), 1);
                fragments++;
                Core.Verification.Checks.CheckInstrumentation.Hit("repair/fragment-repaired");
            }
        }

        return new RepairTally
        {
            Adjudicated = adjudicated,
            SpacesInserted = spaces,
            FragmentsRepaired = fragments,
            LinesRetranslated = whole,
            GeneratedTokens = tokens,
        };
    }

    /// <summary>
    /// Puts a space back where a lowercase letter meets an uppercase one inside
    /// the offending token. Located by searching the CURRENT line rather than by
    /// a stored offset, because an earlier repair on the same line may already
    /// have moved everything after it.
    /// </summary>
    private static bool InsertSpace(string[] lines, int index, string token)
    {
        var match = Regex.Match(
            lines[index], @"(?<![\p{L}])" + Regex.Escape(token) + @"(?![\p{L}])");

        if (!match.Success)
        {
            return false;
        }

        for (var k = 1; k < token.Length; k++)
        {
            if (char.IsLower(token[k - 1]) && char.IsUpper(token[k]))
            {
                lines[index] = lines[index].Insert(match.Index + k, " ");
                return true;
            }
        }

        return false;
    }
}
