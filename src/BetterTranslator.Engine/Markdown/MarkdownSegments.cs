namespace BetterTranslator.Engine.Markdown;

/// <summary>
/// One piece of markup lifted out of a unit, with the sentinel left in its
/// place. The sentinel form is <see cref="Markup.MarkupGuard"/>'s -- brackets
/// and digits, no words -- because that is the form measured to survive every
/// model this application runs, and a second form would be a second thing to
/// get wrong.
/// </summary>
public sealed record MarkdownGuard(string Sentinel, string Original);

/// <summary>
/// One run of translatable text and where it sits in the source document. The
/// fallback path translates these one at a time; the normal path never touches
/// them individually.
/// </summary>
public sealed record MarkdownRun(int Start, int Length);

/// <summary>
/// One translation unit: everything translatable in a single block, as one
/// string, with markup replaced by sentinels.
///
/// A unit is a block rather than a run because a sentence broken across bold,
/// a link and plain text is still one sentence, and four separate calls give
/// four separately-guessed grammars. <see cref="Text"/> is what the model is
/// shown; <see cref="Start"/> and <see cref="Length"/> are where the answer is
/// spliced back into the original document.
/// </summary>
public sealed record MarkdownUnit(
    int Start,
    int Length,
    string Text,
    IReadOnlyList<MarkdownGuard> Guards,
    IReadOnlyList<MarkdownRun> Runs);

/// <summary>How a document came out, for the caller to report or log.</summary>
/// <param name="Text">
/// The translated document, or the source unchanged when the structural
/// backstop refused the result.
/// </param>
/// <param name="Translated">Units the model answered and that validated.</param>
/// <param name="Recovered">
/// Units whose answer failed validation and were retranslated run by run. Not
/// an error: the text is translated, just with less context per call.
/// </param>
/// <param name="Kept">Units nothing usable came back for, left in the source language.</param>
/// <param name="StructureIssues">
/// Empty when the document came through intact. Non-empty means the whole
/// result was discarded and <see cref="Text"/> is the source.
/// </param>
public sealed record MarkdownTranslationResult(
    string Text,
    int Translated,
    int Recovered,
    int Kept,
    IReadOnlyList<string> StructureIssues,
    int Unverified = 0,
    bool Stopped = false)
{
    public bool StructureHeld => StructureIssues.Count == 0;

    public IReadOnlyList<Verification.Structure.SegmentTrace> Segments { get; init; } = [];
}
