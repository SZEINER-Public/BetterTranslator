using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BetterTranslator.App.Services;
using BetterTranslator.Core.Models;
using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Markdown;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

/// <summary>One entry in the chat history.</summary>
public sealed partial class EntryViewModel : ObservableObject
{
    /// <summary>
    /// Holds the tick after a copy, then puts the button back. One timer for
    /// both buttons, since only one of them can have just been pressed. Built
    /// on the first copy rather than in the constructor, so an entry can be
    /// made without a running application to read tokens from.
    /// </summary>
    private DispatcherTimer? _copiedTimer;

    /// <summary>
    /// How many of these have ever been built, for the performance harness. A
    /// cancelled load is only observable as work that did not happen, and the
    /// collection cannot report it: what the surface ends up holding says
    /// nothing about how many were constructed on the way there.
    /// </summary>
    public static long ConstructedCount;

    public EntryViewModel(Entry entry)
    {
        System.Threading.Interlocked.Increment(ref ConstructedCount);

        Entry = entry;
        GeneratedTokens = entry.GeneratedTokens;
        DurationMs = entry.DurationMs;

        // A stored row says what happened, not what is happening: nothing is in
        // flight at load.
        //
        // A result identical to its source was not translated. Rows written
        // before the send path stopped seeding the result with the source read
        // exactly like that, and loading one would put the source straight back
        // into the region this whole shape exists to keep it out of -- and would
        // hand it to the result's copy button as though it were the answer.
        // Dropped rather than migrated: it costs one comparison at load and no
        // rewrite of a database the user may be halfway through reading.
        //
        // Done with no text is not an answer either, whatever the column says.
        var translated = entry.Result.Length > 0
            && !string.Equals(entry.Result, entry.Source, StringComparison.Ordinal);

        Result = translated ? entry.Result : string.Empty;

        Phase = entry.State switch
        {
            EntryState.Done when translated => TranslationPhase.Complete,
            EntryState.Failed => TranslationPhase.Failed,
            _ => TranslationPhase.Idle,
        };

        // Decided once, from the source, and never from the result: the two are
        // the same document in two languages, so a translation that came back
        // damaged must not be able to turn a Markdown message into a plain one
        // or the reverse. The source is also what the sender actually wrote.
        //
        // JSON is asked first. A resource file is not Markdown even when a
        // parser will accept it as such, and the two views are different enough
        // that guessing wrong shows.
        IsJson = JsonSyntax.LooksLikeJson(entry.Source);
        IsMarkdown = !IsJson && MarkdownSyntax.HasStructure(entry.Source);
    }

    public Entry Entry { get; }

    public EntryKind Kind => Entry.Kind;

    public string Source => Entry.Source;

    /// <summary>
    /// How long the send was. The placeholder is laid out from it, so it is the
    /// only thing about the source that crosses into the result region -- a
    /// count, never a character of it.
    /// </summary>
    public int SourceLength => Source.Length;

    public IReadOnlyList<int> SourceLineLengths =>
        _sourceLineLengths ??= Source.ReplaceLineEndings("\n").Split('\n').Select(l => l.Length).ToArray();

    private IReadOnlyList<int>? _sourceLineLengths;

    public string? Context => Entry.Context;

    /// <summary>The time rule between entries, for example "02:06".</summary>
    public string TimeMarker => Entry.CreatedAt.ToString("HH:mm", CultureInfo.CurrentCulture);

    private static readonly Engine.Languages.LanguageRegistry Registry = new();

    /// <summary>
    /// Heads the result with the language it was translated into, as the
    /// registry names it. Entries written before the language was recorded fall
    /// back to the generic result label rather than claiming a language the
    /// database never held.
    /// </summary>
    public string TargetHeading
    {
        get
        {
            var name = Registry.Resolve(Entry.TargetLanguage)?.Name;

            return name is { Length: > 0 }
                ? name.ToUpper(CultureInfo.CurrentCulture)
                : Resources.Strings.EntryResultLabel;
        }
    }

    /// <summary>
    /// Heads the source column. The registry's name for the language the reader
    /// chose, or the generic label while the source is detected or unknown. Set
    /// by the workspace, which owns the direction.
    /// </summary>
    [ObservableProperty]
    public partial string SourceHeading { get; set; } = Resources.Strings.EntrySourceLabel;

    public bool IsWords => Kind == EntryKind.Words;

    public bool IsSentence => Kind == EntryKind.Sentence;

    public bool IsFile => Kind == EntryKind.File;

    public string FileSizeLabel =>
        Entry.FileSizeBytes is { } bytes ? Core.Services.ByteSize.Format(bytes) : string.Empty;

    public bool HasFileSize => Entry.FileSizeBytes is not null;

    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    /// <summary>
    /// Where the produced artifact stands, as one value rather than as four
    /// booleans that can disagree. The right slot renders from this.
    /// </summary>
    public FileSlotState FileState
    {
        get
        {
            if (IsQueued)
            {
                return FileSlotState.Queued;
            }

            if (Phase is TranslationPhase.Pending or TranslationPhase.Streaming)
            {
                return FileSlotState.Translating;
            }

            return HasResultText ? FileSlotState.Done : FileSlotState.Failed;
        }
    }

    public bool ShowsFileQueued => IsFile && FileState == FileSlotState.Queued;

    public bool ShowsFileTranslating => IsFile && FileState == FileSlotState.Translating;

    public bool ShowsFileDone => IsFile && FileState == FileSlotState.Done;

    public bool ShowsFileFailed => IsFile && FileState == FileSlotState.Failed;

    /// <summary>
    /// True only once the engine reports how far a file job has come. Nothing
    /// renders a percentage before then: the skeleton stands in for a figure
    /// that is not known.
    /// </summary>
    public bool HasFileProgress => FileProgress is not null;

    [ObservableProperty]
    public partial double? FileProgress { get; set; }

    partial void OnFileProgressChanged(double? value) => OnPropertyChanged(nameof(HasFileProgress));

    partial void OnIsQueuedChanged(bool value) => RaiseFileSlot();

    private void RaiseFileSlot()
    {
        OnPropertyChanged(nameof(FileState));
        OnPropertyChanged(nameof(ShowsFileQueued));
        OnPropertyChanged(nameof(ShowsFileTranslating));
        OnPropertyChanged(nameof(ShowsFileDone));
        OnPropertyChanged(nameof(ShowsFileFailed));
        OnPropertyChanged(nameof(ShowsFileProgressBar));
        OnPropertyChanged(nameof(ShowsFileSkeleton));
        OnPropertyChanged(nameof(ResultFileName));
        OnPropertyChanged(nameof(HasResultFileName));
        OnPropertyChanged(nameof(CanExportResult));
    }

    public string? FileName => Entry.FileName;

    public string? FilePath => Entry.FilePath;

    /// <summary>
    /// What the produced artifact is called. There is no name until the job has
    /// finished, and nothing invents one: until then the right slot shows the
    /// source name greyed as a placeholder.
    /// </summary>
    public string? ResultFileName => FileState == FileSlotState.Done ? Entry.FileName : null;

    public bool HasResultFileName => ResultFileName is { Length: > 0 };

    public bool CanExportSource => IsFile && Source.Length > 0;

    public bool CanExportResult => IsFile && FileState == FileSlotState.Done;

    /// <summary>
    /// What the save dialog opens on. The stem and the extension are the source
    /// file's own, and the code between them is the registry's for the language
    /// this was translated into, so nothing here is invented.
    /// </summary>
    public string ExportFileName
    {
        get
        {
            var name = Entry.FileName ?? string.Empty;

            if (name.Length == 0)
            {
                return string.Empty;
            }

            var code = Registry.Resolve(Entry.TargetLanguage)?.Code;

            if (string.IsNullOrEmpty(code))
            {
                return name;
            }

            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);

            return $"{stem}.{code}{extension}";
        }
    }

    public bool ShowsFileProgressBar => ShowsFileTranslating && HasFileProgress;

    public bool ShowsFileSkeleton => ShowsFileTranslating && !HasFileProgress;

    [ObservableProperty]
    public partial bool IsSourcePreviewOpen { get; set; }

    [ObservableProperty]
    public partial bool IsResultPreviewOpen { get; set; }

    /// <summary>
    /// A message that is not a file has no preview to open, so its body is
    /// always the open state. One flag drives one animated area either way.
    /// </summary>
    public bool IsSourceBodyOpen => !IsFile || IsSourcePreviewOpen;

    public bool IsResultBodyOpen => !IsFile || IsResultPreviewOpen;

    public string SourcePreviewState => PreviewState(IsSourcePreviewOpen);

    public string ResultPreviewState => PreviewState(IsResultPreviewOpen);

    private static string PreviewState(bool open) =>
        open ? Resources.Strings.EntryPreviewExpanded : Resources.Strings.EntryPreviewCollapsed;

    partial void OnIsSourcePreviewOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSourceBodyOpen));
        OnPropertyChanged(nameof(SourcePreviewState));
        RaiseDisclosure();
    }

    partial void OnIsResultPreviewOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsResultBodyOpen));
        OnPropertyChanged(nameof(ResultPreviewState));
        RaiseDisclosure();
    }

    /// <summary>
    /// What opening or shutting a preview moves besides the body itself. Both
    /// the format switch and Show more follow whether a body is on screen, and a
    /// flag that changes without this is a control that keeps whatever its
    /// binding last read.
    /// </summary>
    private void RaiseDisclosure()
    {
        // Shutting the last preview takes the control away with it, so the open
        // state goes too. Left set, reopening the eye would show Show less over
        // a preview the reader never expanded.
        if (IsFile && !IsSourcePreviewOpen && !IsResultPreviewOpen)
        {
            IsExpanded = false;
        }

        OnPropertyChanged(nameof(ShowsFormatToggle));
        OnPropertyChanged(nameof(CanCollapse));
        OnPropertyChanged(nameof(IsCollapsed));
    }

    [RelayCommand]
    private void ToggleSourcePreview() => IsSourcePreviewOpen = !IsSourcePreviewOpen;

    [RelayCommand]
    private void ToggleResultPreview() => IsResultPreviewOpen = !IsResultPreviewOpen;

    [ObservableProperty]
    public partial string Result { get; set; }

    /// <summary>
    /// The terms memory decided inside the result, with where they sit. Set
    /// after a lookup; empty until then.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<Indexing.Retrieval.MemoryMatch> Matches { get; set; } = [];

    /// <summary>The unsure threshold in force, so the marks can be recomputed.</summary>
    [ObservableProperty]
    public partial int UnsureThreshold { get; set; } = 80;

    /// <summary>
    /// The marks that line up with the text they name.
    ///
    /// A lookup measures its offsets against the phrase it was handed -- the
    /// source -- and the region renders the translation, so a mark applies only
    /// where the same characters happen to sit at the same place. Carrying the
    /// rest across would underline whichever words the stale offsets landed on,
    /// and it is what the swap to a real translation would otherwise start
    /// doing now that the source no longer stands in for the result.
    /// </summary>
    private IReadOnlyList<Indexing.Retrieval.MemoryMatch> Aligned =>
    [
        .. Matches.Where(m =>
            m.Start >= 0
            && m.Start + m.Length <= Result.Length
            && string.Equals(Result.Substring(m.Start, m.Length), m.Term, StringComparison.Ordinal))
    ];

    /// <summary>
    /// The result split into runs, each plain or carrying a match. The view
    /// renders one Run per segment.
    /// </summary>
    public IReadOnlyList<MemorySegmentViewModel> Segments =>
        new Indexing.Retrieval.MemoryAnnotatedResult(Result, Aligned)
            .Segments()
            .Select(s => new MemorySegmentViewModel(s.Text, s.Match, UnsureThreshold))
            .ToList();

    public bool HasMatches => Aligned.Count > 0;

    /// <summary>
    /// What this entry cost, shown under it in Advanced. Null until a model has
    /// actually run: an entry answered from memory, or one stored before this
    /// was recorded, has no measurement and says nothing rather than "0".
    /// </summary>
    [ObservableProperty]
    public partial int? GeneratedTokens { get; set; }

    [ObservableProperty]
    public partial int? DurationMs { get; set; }

    public bool HasMetrics => GeneratedTokens is > 0 && DurationMs is not null;

    /// <summary>
    /// "4 156 tokens", grouped through the current culture so a four-digit
    /// count is one word rather than a place the row can wrap. Generated
    /// tokens only; the tooltip says why.
    /// </summary>
    public string TokenFigure =>
        !HasMetrics
            ? string.Empty
            : GeneratedTokens!.Value == 1
                ? "1 token"
                : string.Create(CultureInfo.CurrentCulture, $"{GeneratedTokens.Value:N0} tokens");

    /// <summary>"57 s", or "840 ms" for work that finished inside a second.</summary>
    public string DurationFigure => HasMetrics ? FormatDuration(DurationMs!.Value) : string.Empty;

    /// <summary>
    /// "72,9 tok/s", and empty for a run measured at zero: a rate divided out
    /// of no elapsed time is a speed nobody took.
    /// </summary>
    public string RateFigure
    {
        get
        {
            if (!HasMetrics)
            {
                return string.Empty;
            }

            var seconds = DurationMs!.Value / 1000d;

            return seconds > 0
                ? string.Create(CultureInfo.CurrentCulture, $"{GeneratedTokens!.Value / seconds:0.#} tok/s")
                : string.Empty;
        }
    }

    public static string MetricsTooltip =>
        "Generated tokens only: the runtime exports no tokenizer, so the prompt is not counted.";

    private Engine.Markup.AuditReport? _fidelity;

    /// <summary>
    /// Whether the audit figures are wanted at all. Set by the workspace from the
    /// Advanced switch, and read before anything is measured: a binding on a
    /// collapsed element still evaluates, so gating the view alone would pay for
    /// a measure nobody is looking at on every entry in the conversation.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsFidelity { get; set; }

    private Engine.Markup.AuditReport? Fidelity
    {
        get
        {
            if (!ShowsFidelity || !HasResultText)
            {
                return null;
            }

            return _fidelity ??= Engine.Markup.TranslationAudit.Run(Entry.Source, Result);
        }
    }

    partial void OnShowsFidelityChanged(bool value) => RaiseFidelity();

    /// <summary>
    /// Whether there is a measure worth stating. A pair with nothing
    /// translatable in it has none: the audit masks code spans, links and
    /// quoted text before it counts, so a JSON resource file or a message that
    /// is one fenced block leaves no denominator, and a percentage over none of
    /// them would read as "0% translated" over work that was done correctly.
    /// </summary>
    public bool HasFidelity => Figures is not null;

    private AuditFigures? _figures;

    /// <summary>
    /// The measure, turned into the strings the row draws and then held. Split
    /// into cells without this, a row pays for the work twice over: the line
    /// count walks both texts and the quoted literals run their regexes over
    /// them, once per figure that asks.
    /// </summary>
    private readonly record struct AuditFigures(
        string Coverage,
        string Lines,
        bool LinesDiffer,
        string Units,
        bool UnitsDiffer,
        string Defects,
        bool HasDefects);

    private AuditFigures? Figures =>
        Fidelity is { Coverage.TranslatableTokens: > 0 } report ? _figures ??= Measure(report) : null;

    private AuditFigures Measure(Engine.Markup.AuditReport report)
    {
        var sourceLines = Engine.Markup.LineParity.Count(Entry.Source);
        var resultLines = Engine.Markup.LineParity.Count(Result);

        var defects = new List<string>();

        var kept = report.Spans.Count(span => !span.Preserved);

        if (kept > 0)
        {
            defects.Add(kept == 1 ? "1 span kept" : $"{kept} spans kept");
        }

        var citations = Engine.Markup.TranslationAudit.QuotedLiteralsLost(Entry.Source, Result).Count;

        if (citations > 0)
        {
            defects.Add(citations == 1 ? "1 citation lost" : $"{citations} citations lost");
        }

        if (report.Structural.Count > 0)
        {
            defects.Add(report.Structural.Count == 1
                ? "1 markup defect"
                : $"{report.Structural.Count} markup defects");
        }

        return new AuditFigures(
            string.Create(CultureInfo.CurrentCulture, $"{report.Coverage.Percent:0.#}% translated"),
            $"{sourceLines}/{resultLines} lines",
            sourceLines != resultLines,
            $"{report.SourceUnits}/{report.TargetUnits} units",
            report.SourceUnits != report.TargetUnits,
            defects.Count == 0 ? "no defects" : string.Join(", ", defects),
            defects.Count > 0);
    }

    /// <summary>"96,7% translated", the anchor the rest of the row hangs off.</summary>
    public string CoverageFigure => Figures?.Coverage ?? string.Empty;

    /// <summary>
    /// "191/186 lines", and empty while the two sides agree. An equal pair is
    /// the expected outcome and the widest thing this row can hold for the
    /// least it can say, so it earns its width only when it is news.
    /// </summary>
    public string LineFigure => Figures is { LinesDiffer: true } figures ? figures.Lines : string.Empty;

    /// <summary>"119/117 units", on the same terms as the lines.</summary>
    public string UnitFigure => Figures is { UnitsDiffer: true } figures ? figures.Units : string.Empty;

    /// <summary>
    /// "3 spans kept, 3 markup defects", or "no defects" when the audit found
    /// none. Said rather than left out, so a clean entry is not something a
    /// reader infers from an absence.
    /// </summary>
    public string DefectFigure => Figures?.Defects ?? string.Empty;

    /// <summary>Whether anything was found. Drives the colour of that one cell.</summary>
    public bool HasDefects => Figures?.HasDefects ?? false;

    public static string FidelityTooltip =>
        "Of the words that could be translated, the share that came back translated.";

    /// <summary>
    /// The three terms in the findings cell, glossed once. A reader cannot act
    /// on a count whose noun means nothing to them, and these are the only
    /// figures here anyone can act on.
    /// </summary>
    public static string DefectsTooltip =>
        "Source text left in place, quoted names the output dropped, and Markdown that came back broken.";

    private void RaiseFidelity()
    {
        OnPropertyChanged(nameof(HasFidelity));
        OnPropertyChanged(nameof(CoverageFigure));
        OnPropertyChanged(nameof(LineFigure));
        OnPropertyChanged(nameof(UnitFigure));
        OnPropertyChanged(nameof(DefectFigure));
        OnPropertyChanged(nameof(HasDefects));
    }

    /// <summary>
    /// Sub-second work reads in milliseconds; anything longer in seconds. A
    /// translation that took 0.9 s and one that took 900 ms are the same fact,
    /// and the one people are waiting through is the one worth rounding.
    /// </summary>
    internal static string FormatDuration(int milliseconds) =>
        milliseconds < 1000
            ? string.Create(CultureInfo.CurrentCulture, $"{milliseconds} ms")
            : string.Create(CultureInfo.CurrentCulture, $"{milliseconds / 1000d:0.#} s");

    // Each property writes back only its own field, as Result and State already
    // do. Writing both from one place cost an afternoon: the constructor sets
    // GeneratedTokens first, that fired the shared write-back, and the write-back
    // pushed the still-null DurationMs onto the entry -- destroying the value the
    // very next line was about to read. Every reloaded entry came back with its
    // tokens and no duration.
    partial void OnGeneratedTokensChanged(int? value)
    {
        Entry.GeneratedTokens = value;
        RaiseMetrics();
    }

    partial void OnDurationMsChanged(int? value)
    {
        Entry.DurationMs = value;
        RaiseMetrics();
    }

    private void RaiseMetrics()
    {
        OnPropertyChanged(nameof(HasMetrics));
        OnPropertyChanged(nameof(TokenFigure));
        OnPropertyChanged(nameof(DurationFigure));
        OnPropertyChanged(nameof(RateFigure));
    }

    /// <summary>
    /// Where this entry stands. The single fact the result region renders from,
    /// alongside <see cref="Result"/>.
    /// </summary>
    [ObservableProperty]
    public partial TranslationPhase Phase { get; set; }

    /// <summary>
    /// True while the placeholder is up. Not the same question as Pending: the
    /// request is out first and the placeholder follows only if the wait is long
    /// enough to be worth marking.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsSkeleton { get; set; }

    /// <summary>
    /// D2: a failed entry replaces the result region with a line saying so and
    /// offers Retry. No toast, and never the source in place of the answer.
    /// </summary>
    public bool HasFailed => Phase == TranslationPhase.Failed;

    /// <summary>
    /// There is a translation to show. Both halves are load-bearing: the phase
    /// says an answer arrived, the length says it is not the empty string a
    /// cleared entry carries.
    /// </summary>
    public bool HasResultText =>
        Phase is TranslationPhase.Complete or TranslationPhase.Streaming && Result.Length > 0;

    /// <summary>
    /// The message was written as Markdown, so it is rendered rather than shown
    /// as its own syntax, and it carries a View/Source switch. False for
    /// ordinary prose, which gets neither.
    /// </summary>
    public bool IsMarkdown { get; }

    /// <summary>
    /// The message is a JSON document, so it is shown as keys against values and
    /// carries the same View/Source switch a Markdown message does.
    /// </summary>
    public bool IsJson { get; }

    /// <summary>
    /// Whether there are two ways of reading this message at all. Ordinary prose
    /// has one, and a switch between a view and itself is a control that does
    /// nothing.
    /// </summary>
    public bool HasFormatToggle => IsMarkdown || IsJson;

    /// <summary>
    /// Whether the rendered and source switch is offered on the row.
    ///
    /// It follows the body it acts on. A file row starts as two chips with an eye
    /// on each and no body on screen, and the switch there acted on nothing, so it
    /// read as a dead control. Open either preview and the body is on screen, so
    /// the choice between rendered and source means something again.
    ///
    /// Separate from <see cref="HasFormatToggle"/> because that one still decides
    /// how a body renders, and a file's body is Markdown whether it is shown or not.
    /// </summary>
    public bool ShowsFormatToggle =>
        HasFormatToggle && (!IsFile || IsSourcePreviewOpen || IsResultPreviewOpen);

    /// <summary>
    /// Which half of the switch is lit. Its own property rather than the
    /// negation of IsSourceMode at the use site, because a plain message is in
    /// neither state and must not light the View half of a switch it does not
    /// have.
    /// </summary>
    public bool ShowsRendered => HasFormatToggle && !IsSourceMode;

    /// <summary>
    /// The switch. One per message rather than one per side: the whole point of
    /// reading the two columns together is that they are the same document, and
    /// comparing a rendered source against raw translated Markdown compares
    /// nothing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSourceMode { get; set; }

    [RelayCommand]
    private void ToggleFormat() => IsSourceMode = !IsSourceMode;

    /// <summary>
    /// The sent message as blocks. Parsed once: the source cannot change, and a
    /// property that reparses on every binding read would do it on every layout
    /// pass of a scrolling transcript.
    /// </summary>
    public IReadOnlyList<Indexing.Markdown.MdBlock> SourceBlocks =>
        _sourceBlocks ??= Indexing.Markdown.MarkdownBlockParser.Parse(Source);

    private IReadOnlyList<Indexing.Markdown.MdBlock>? _sourceBlocks;

    /// <summary>
    /// The translation as blocks. Reparsed when the result changes, which is
    /// when it arrives and when a memory word is swapped inside it.
    /// </summary>
    public IReadOnlyList<Indexing.Markdown.MdBlock> ResultBlocks =>
        _resultBlocks ??= Indexing.Markdown.MarkdownBlockParser.Parse(Result);

    private IReadOnlyList<Indexing.Markdown.MdBlock>? _resultBlocks;

    /// <summary>
    /// The document's keys against its values, on each side. Every scalar is
    /// listed, not only the translatable ones: a reader comparing the two
    /// columns needs to see that the numbers and the booleans did not move
    /// either.
    /// </summary>
    public IReadOnlyList<JsonScalar> SourceRows =>
        _sourceRows ??= IsJson ? JsonSegmenter.Segment(Source) ?? [] : [];

    private IReadOnlyList<JsonScalar>? _sourceRows;

    public IReadOnlyList<JsonScalar> ResultRows =>
        _resultRows ??= IsJson ? JsonSegmenter.Segment(Result) ?? [] : [];

    private IReadOnlyList<JsonScalar>? _resultRows;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>
    /// Whether the row offers Show more at all.
    ///
    /// A file row offers it only once a preview is open. Shut, the row is two
    /// chips and there is no body for Show more to lengthen, so the control did
    /// nothing when pressed. Opening either eye gives it something to act on.
    /// </summary>
    public bool CanCollapse =>
        IsFile
            ? IsSourcePreviewOpen || IsResultPreviewOpen
            : Source.Length > CollapseThreshold || Result.Length > CollapseThreshold;

    public bool IsCollapsed => CanCollapse && !IsExpanded;

    public string DisclosureLabel => IsExpanded ? "Show less" : "Show more";

    private const int CollapseThreshold = 900;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>
    /// Expanding moves the whole surface, not just the disclosure.
    ///
    /// Every flag the surface renders from moves with it. Raising only some of
    /// them left the rest holding the value their binding last read:
    /// ShowsPlainResult stayed
    /// true, so a translated Markdown file drew its raw text underneath its
    /// rendered blocks, overlapping. RaiseSurface is the complete list and is
    /// called rather than restated, so a flag added there cannot be forgotten here.
    /// </summary>
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCollapsed));
        OnPropertyChanged(nameof(DisclosureLabel));
        RaiseSurface();
    }

    /// <summary>Rendered Markdown, on each side.</summary>
    public bool ShowsSourceMarkdown => IsMarkdown && !IsSourceMode;

    /// <summary>The document as keys and values.</summary>
    public bool ShowsSourceJson => IsJson && !IsSourceMode;

    /// <summary>The raw text, monospaced. What Copy puts on the clipboard.</summary>
    public bool ShowsSourceSyntax => HasFormatToggle && IsSourceMode;

    /// <summary>An ordinary message, shown as itself.</summary>
    public bool ShowsSourceProse => !HasFormatToggle;

    public bool ShowsWordSource => IsWords && ShowsSourceProse;

    public bool ShowsSentenceSource => !IsWords && ShowsSourceProse;

    public bool ShowsWordResult => IsWords && ShowsPlainResult;

    public bool ShowsSentenceResult => !IsWords && ShowsPlainResult;

    public bool ShowsResultMarkdown => HasResultText && IsMarkdown && !IsSourceMode;

    public bool ShowsResultJson => HasResultText && IsJson && !IsSourceMode;

    public bool ShowsResultSyntax => HasResultText && HasFormatToggle && IsSourceMode;

    /// <summary>
    /// The translation with its memory terms marked. Prose only: a Markdown
    /// result is rendered from its own blocks, and marking runs inside that
    /// would mean a second renderer that had to agree with the first.
    /// </summary>
    public bool ShowsMarkedResult => HasResultText && !HasFormatToggle && HasMatches;

    /// <summary>
    /// What the verifier said about this translation, or null when it did not
    /// run. Read only by the preview: verification never gates the result.
    /// </summary>
    [ObservableProperty]
    public partial Core.Verification.VerificationResult? Verification { get; set; }

    /// <summary>
    /// The translation with the verifier's underlines on it. Only when there is
    /// something to underline, so a clean answer renders through the selectable
    /// text box it always did.
    /// </summary>
    public bool ShowsVerifiedResult =>
        HasResultText && !HasFormatToggle && !HasMatches && Verification is { HasFindings: true };

    /// <summary>The plain translation, before memory has marked anything in it.</summary>
    public bool ShowsPlainResult => HasResultText && !HasFormatToggle && !HasMatches && !ShowsVerifiedResult;

    /// <summary>
    /// Why this entry has no translation. A gate that refuses an answer produces
    /// no text and no reason on screen, which reads as an application that did
    /// nothing -- so the reason is shown with the failure rather than left in a
    /// property nobody reads.
    /// </summary>
    [ObservableProperty]
    public partial string? Note { get; set; }

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(HasNote));

    partial void OnPhaseChanged(TranslationPhase value)
    {
        // The stored state is the durable half of the phase. Idle and Pending
        // are both "nothing has translated it", which is what the column means.
        Entry.State = value switch
        {
            TranslationPhase.Complete => EntryState.Done,
            TranslationPhase.Failed => EntryState.Failed,
            _ => EntryState.Pending,
        };

        RaiseSurface();
    }

    partial void OnResultChanged(string value)
    {
        Entry.Result = value;
        _resultBlocks = null;
        _resultRows = null;
        _fidelity = null;
        _figures = null;
        OnPropertyChanged(nameof(ResultBlocks));
        OnPropertyChanged(nameof(ResultRows));
        OnPropertyChanged(nameof(CanCollapse));
        OnPropertyChanged(nameof(IsCollapsed));
        RaiseFidelity();
        RaiseSegments();
    }

    partial void OnIsSourceModeChanged(bool value) => RaiseSurface();

    partial void OnVerificationChanged(Core.Verification.VerificationResult? value) => RaiseSurface();

    partial void OnMatchesChanged(IReadOnlyList<Indexing.Retrieval.MemoryMatch> value) => RaiseSegments();

    /// <summary>
    /// Moving the threshold re-marks what is already on screen; nothing is
    /// translated again.
    /// </summary>
    partial void OnUnsureThresholdChanged(int value) => RaiseSegments();

    private void RaiseSegments()
    {
        OnPropertyChanged(nameof(Segments));
        OnPropertyChanged(nameof(HasMatches));
        RaiseSurface();
    }

    /// <summary>
    /// What the result region shows. Raised from both halves it depends on,
    /// because either can move without the other: a rewritten word changes the
    /// text at a settled phase, and a failure changes the phase with no text at
    /// all.
    /// </summary>
    private void RaiseSurface()
    {
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasResultText));
        OnPropertyChanged(nameof(ShowsPlainResult));
        OnPropertyChanged(nameof(ShowsMarkedResult));
        OnPropertyChanged(nameof(ShowsVerifiedResult));
        OnPropertyChanged(nameof(ShowsRendered));
        OnPropertyChanged(nameof(ShowsSourceMarkdown));
        OnPropertyChanged(nameof(ShowsSourceJson));
        OnPropertyChanged(nameof(ShowsSourceSyntax));
        OnPropertyChanged(nameof(ShowsResultMarkdown));
        OnPropertyChanged(nameof(ShowsResultJson));
        OnPropertyChanged(nameof(ShowsResultSyntax));
        OnPropertyChanged(nameof(HasFormatToggle));
        OnPropertyChanged(nameof(ShowsFormatToggle));
        OnPropertyChanged(nameof(ShowsSourceProse));
        OnPropertyChanged(nameof(ShowsWordSource));
        OnPropertyChanged(nameof(ShowsSentenceSource));
        OnPropertyChanged(nameof(ShowsWordResult));
        OnPropertyChanged(nameof(ShowsSentenceResult));
        RaiseFileSlot();

        // Whether there is a translation to copy is the same phase question, and
        // a menu item reads it through CanExecute rather than through a binding.
        OnPropertyChanged(nameof(CanCopyResult));
        CopyResultCommand.NotifyCanExecuteChanged();

        // The audit figures hang off HasResultText, which is a phase question,
        // so they belong to this surface too. Apply writes the text first and
        // the phase second: the write-back for the text asks them while the
        // phase still says nothing has landed, so every figure answers empty
        // and the row that reads them stays collapsed for the whole run. Only
        // an unrelated re-raise later brought it back, which is why it showed
        // on a reopened chat and not on the translation that produced it.
        RaiseFidelity();
    }

    /// <summary>
    /// Cancels whatever run is out for this entry, and counts them. Both are
    /// needed and neither replaces the other: cancelling stops the work, the
    /// count decides whether an answer that arrives anyway is still wanted. A
    /// runtime that reports a cancelled generation as "nothing came back" would
    /// otherwise land a failure on the run that superseded it.
    /// </summary>
    private CancellationTokenSource? _run;

    private int _sequence;

    /// <summary>
    /// Drives both waits around the placeholder, never overlapping: first the
    /// delay before it appears, then the floor it is held for. One timer,
    /// because the second only ever starts once the first is done with.
    /// </summary>
    private DispatcherTimer? _skeletonTimer;

    private long _shownAtMs;

    /// <summary>An answer waiting out the placeholder's minimum hold.</summary>
    private (TranslationPhase Phase, string? Text, string? Note)? _settling;

    /// <summary>
    /// Starts a run. Anything already in flight for this entry is superseded:
    /// cancelled, and its answer discarded if it arrives regardless. The result
    /// is cleared before the wait begins, so a retry never leaves the previous
    /// answer sitting under the new one.
    /// </summary>
    /// <returns>
    /// The token the work runs under, and the sequence the caller quotes back in
    /// <see cref="Owns"/> before it writes anything.
    /// </returns>
    public (int Sequence, CancellationToken Token) BeginRun()
    {
        // Cancelled but not disposed. The runtime links its own source to this
        // token and unregisters in its finally, so the cancelled one is garbage
        // the moment its run lets go -- and disposing it out from under a call
        // that is still unwinding is a race for nothing.
        _run?.Cancel();
        _run = new CancellationTokenSource();
        _sequence++;

        // Matches are left alone: they belong to the source, not to the run, and
        // Aligned renders none of them while there is no result to sit in.
        _settling = null;
        ShowsSkeleton = false;
        Note = null;
        Result = string.Empty;
        Phase = TranslationPhase.Pending;

        // No token to read means no screen to read it for, so nothing is owed a
        // delay and the answer lands as it arrives.
        if (Tokens.TryMilliseconds("SkeletonRevealDelayMs") is { } delay)
        {
            Restart(delay);
        }

        return (_sequence, _run.Token);
    }

    /// <summary>
    /// Stops the run that is out without starting another, for the one case that
    /// has no next run: the row is going away. The sequence moves too, so an
    /// answer already on its way back cannot land on an entry that no longer
    /// exists.
    /// </summary>
    public void CancelRun()
    {
        _run?.Cancel();
        _sequence++;
    }

    /// <summary>
    /// True when the run that quoted this sequence is still the current one. A
    /// slower earlier request cannot overwrite a newer result.
    /// </summary>
    public bool Owns(int sequence) => sequence == _sequence;

    /// <summary>
    /// The answer arrived. <paramref name="note"/> is anything worth saying
    /// about how it got here -- a Markdown block that had to be retranslated
    /// phrase by phrase reads unevenly, and that is better explained than
    /// wondered about.
    /// </summary>
    public void Complete(string text, string? note = null) =>
        Settle(TranslationPhase.Complete, text, note);

    /// <summary>
    /// Nothing came back, or a gate refused what did. <paramref name="note"/> is
    /// which gate and why, where there is one to name.
    /// </summary>
    public void Fail(string? note) => Settle(TranslationPhase.Failed, text: null, note);

    /// <summary>Nothing ran at all: no runtime is wired up to ask.</summary>
    public void Idle() => Settle(TranslationPhase.Idle, text: null, note: null);

    /// <summary>
    /// Lands an answer, or holds it back until the placeholder has had its
    /// floor. An answer faster than the delay never raised a placeholder and
    /// lands at once, which is what stops a local model flashing one; a slower
    /// one cannot take the placeholder away before it has been on screen long
    /// enough to read as a state rather than as a flicker.
    /// </summary>
    private void Settle(TranslationPhase phase, string? text, string? note)
    {
        _skeletonTimer?.Stop();
        _settling = null;

        if (!ShowsSkeleton)
        {
            Apply(phase, text, note);
            return;
        }

        // Monotonic, so a clock change part way through a slow translation
        // cannot make the placeholder appear to have been up for hours.
        var shownFor = TimeSpan.FromMilliseconds(Environment.TickCount64 - _shownAtMs);
        var floor = Tokens.TryMilliseconds("SkeletonMinHoldMs") ?? TimeSpan.Zero;

        if (shownFor >= floor)
        {
            Apply(phase, text, note);
            return;
        }

        _settling = (phase, text, note);
        Restart(floor - shownFor);
    }

    private void Apply(TranslationPhase phase, string? text, string? note)
    {
        ShowsSkeleton = false;
        Note = note;

        // Text before phase: the phase is what the region reads to decide
        // whether to render at all, so the string has to be there when it does.
        Result = text ?? string.Empty;
        Phase = phase;
        RaiseSurface();
    }

    private void Restart(TimeSpan interval)
    {
        Timer().Stop();
        Timer().Interval = interval;
        Timer().Start();
    }

    /// <summary>
    /// Built on the first run rather than in the constructor, for the same
    /// reason the copy timer is: an entry has to be constructible without a
    /// running application to read tokens from.
    /// </summary>
    private DispatcherTimer Timer()
    {
        if (_skeletonTimer is not null)
        {
            return _skeletonTimer;
        }

        _skeletonTimer = new DispatcherTimer();
        _skeletonTimer.Tick += (_, _) => OnTimerElapsed();

        return _skeletonTimer;
    }

    private void OnTimerElapsed()
    {
        Timer().Stop();

        if (_settling is { } held)
        {
            _settling = null;
            Apply(held.Phase, held.Text, held.Note);
            return;
        }

        // The request outlasted the delay. Anything else means it landed first
        // and the placeholder is no longer owed.
        if (Phase != TranslationPhase.Pending)
        {
            return;
        }

        ShowsSkeleton = true;
        _shownAtMs = Environment.TickCount64;
    }

    /// <summary>True while the tick is standing in for the copy glyph.</summary>
    [ObservableProperty]
    public partial bool SourceCopied { get; set; }

    [ObservableProperty]
    public partial bool ResultCopied { get; set; }

    /// <summary>
    /// Whether there is anything to put on the clipboard.
    ///
    /// Copy used to accept the click and then do nothing, which is invisible
    /// behind a button that hides itself when there is no answer and wrong in a
    /// menu, where the item is there whatever the row holds. The command says so
    /// itself, so every surface that offers it is right without being told.
    /// </summary>
    public bool CanCopySource => Source.Length > 0;

    public bool CanCopyResult => HasResultText;

    [RelayCommand(CanExecute = nameof(CanCopySource))]
    private void CopySource() => Copy(Source, source: true);

    [RelayCommand(CanExecute = nameof(CanCopyResult))]
    private void CopyResult() => Copy(Result, source: false);

    /// <summary>
    /// The clipboard is held by whatever else is running, so a copy can fail
    /// for reasons that are nothing to do with this application. A failed copy
    /// simply does not confirm, rather than throwing out of a click handler.
    /// </summary>
    private void Copy(string text, bool source)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        HoldTimer().Stop();
        SourceCopied = source;
        ResultCopied = !source;
        HoldTimer().Start();
    }

    private DispatcherTimer HoldTimer()
    {
        if (_copiedTimer is not null)
        {
            return _copiedTimer;
        }

        _copiedTimer = new DispatcherTimer { Interval = Tokens.Milliseconds("CopyConfirmHoldMs") };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            SourceCopied = false;
            ResultCopied = false;
        };

        return _copiedTimer;
    }
}
