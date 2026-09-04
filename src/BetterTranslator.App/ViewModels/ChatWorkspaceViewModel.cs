using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using BetterTranslator.App.Services;
using BetterTranslator.Core.Languages;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Markdown;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// The chat sidebar and the chat pane. They share the chat list and the
/// selection, so they share a view model.
/// </summary>
public sealed partial class ChatWorkspaceViewModel : ObservableObject
{
    /// <summary>D6, unconfirmed: a chat is named from the first 40 characters
    /// of its first entry until a model can summarize it.</summary>
    private const int ProvisionalNameLength = 40;

    private readonly ChatStore _store;
    private readonly ClockService _clock;
    private readonly Func<ConfirmDialogViewModel?, ConfirmDialogViewModel?> _showDialog;

    public ChatWorkspaceViewModel(
        ChatStore store,
        ClockService clock,
        Func<ConfirmDialogViewModel?, ConfirmDialogViewModel?> showDialog)
    {
        _store = store;
        _clock = clock;
        _showDialog = showDialog;

        PinnedView = BuildView(pinned: true);
        ChatsView = BuildView(pinned: false);

        LanguagesView = new CollectionViewSource { Source = Languages }.View;
        LanguagesView.Filter = item => item is TargetLanguage language && language.Matches(LanguageSearch);

        SourceLanguagesView = new CollectionViewSource { Source = Languages }.View;
        SourceLanguagesView.Filter = item => item is TargetLanguage language && language.Matches(SourceLanguageSearch);

        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentSummary));
            OnPropertyChanged(nameof(UsesMemory));
            OnPropertyChanged(nameof(CanSend));
        };

        Entries.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<EntryViewModel>() ?? [])
            {
                WatchFold(added);
            }

            OnPropertyChanged(nameof(ExpandedEntry));
            OnPropertyChanged(nameof(HasExpandedEntry));
        };
    }

    public ObservableCollection<ChatRowViewModel> Rows { get; } = [];

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Chips above the composer input. Project memory is always first.
    /// </summary>
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    public FilePreviewViewModel Preview { get; } = new();

    /// <summary>
    /// The whole registry as the model in force sees it: every language, each
    /// carrying whether that model supports it. Filled by the shell, so it
    /// repartitions when Simple is chosen and again for Thinking rather than being
    /// a list maintained in the view.
    /// </summary>
    public ObservableCollection<TargetLanguage> Languages { get; } = [.. TargetLanguage.All];

    public int AvailableLanguageCount => Languages.Count(l => l.IsAvailable);

    /// <summary>What the target picker shows, once the search has been applied.</summary>
    public ICollectionView LanguagesView { get; }

    /// <summary>The same rows, filtered by the source picker's own search.</summary>
    public ICollectionView SourceLanguagesView { get; }

    [ObservableProperty]
    public partial string LanguageSearch { get; set; } = string.Empty;

    partial void OnLanguageSearchChanged(string value)
    {
        LanguagesView.Refresh();
        OnPropertyChanged(nameof(HasLanguageMatches));
        OnPropertyChanged(nameof(NoLanguageMatchNote));
    }

    [ObservableProperty]
    public partial string SourceLanguageSearch { get; set; } = string.Empty;

    partial void OnSourceLanguageSearchChanged(string value) => SourceLanguagesView.Refresh();

    public bool HasLanguageMatches => Languages.Any(l => l.Matches(LanguageSearch));

    /// <summary>
    /// Said instead of an empty list, and it names the model: a language missing
    /// here is usually one this model was never trained on rather than one that
    /// does not exist.
    /// </summary>
    public string? NoLanguageMatchNote =>
        HasLanguageMatches ? null : $"No language matches “{LanguageSearch.Trim()}” in {LanguageModelName}.";

    /// <summary>The model the language list belongs to, named in the picker.</summary>
    [ObservableProperty]
    public partial string LanguageModelName { get; set; } = "this model";

    /// <summary>
    /// Repartitions the picker when the model changes. A language the new model
    /// does not support stays on the list, is shown as unavailable with the
    /// reason, and stays selected: switching model is not a choice about the
    /// pair, so it may not quietly rewrite one. The send is refused instead, by
    /// <see cref="Direction"/>'s verdict, and switching back restores the pair
    /// exactly as it was.
    /// </summary>
    public void SetModelLanguages(IReadOnlyList<TargetLanguage> languages, string modelName)
    {
        LanguageModelName = modelName;

        Languages.Clear();
        foreach (var language in languages)
        {
            Languages.Add(language);
        }

        LanguagesView.Refresh();
        SourceLanguagesView.Refresh();
        OnPropertyChanged(nameof(AvailableLanguageCount));
        OnPropertyChanged(nameof(HasLanguageMatches));
        OnPropertyChanged(nameof(NoLanguageMatchNote));
        RefreshDirection();
    }

    /// <summary>
    /// The model the pair is checked against. Set by the shell alongside
    /// <see cref="SetModelLanguages"/>, and read only by the guard.
    /// </summary>
    public string? ModelId { get; set; }

    [ObservableProperty]
    public partial TargetLanguage Language { get; set; } = TargetLanguage.Default;

    [ObservableProperty]
    public partial TargetLanguage SourceLanguage { get; set; } = TargetLanguage.Source;

    /// <summary>
    /// True while the source is the app's own assumption rather than something
    /// the reader chose. Detection may write the source in this state and in no
    /// other, which is what keeps an explicit choice explicit.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSourceAuto { get; set; } = true;

    /// <summary>
    /// What the text is written in, when something can tell. Returns unknown
    /// rather than guessing, and is never consulted for a source the reader has
    /// already chosen.
    /// </summary>
    public Func<string, LanguageChoice>? DetectSource { get; set; }

    private static readonly DirectionGuard Guard = new();

    /// <summary>
    /// The pair, canonicalized. This property is the single owner of the
    /// direction: <see cref="ChooseLanguage"/>, <see cref="ChooseSourceLanguage"/>,
    /// <see cref="SwapDirection"/> and detection all write through
    /// <see cref="ApplyDirection"/>, and nothing else writes either side.
    /// </summary>
    public TranslationDirection Direction { get; private set; } = TranslationDirection.Of(
        LanguageChoice.Of(TargetLanguage.Source.Code, TargetLanguage.Source.Name, TargetLanguage.Source.Script),
        LanguageChoice.Of(TargetLanguage.Default.Code, TargetLanguage.Default.Name, TargetLanguage.Default.Script));

    public DirectionVerdict DirectionVerdict { get; private set; } = DirectionVerdict.Ready;

    /// <summary>Why the pair cannot be sent, worded by the resource path, or null.</summary>
    public string? DirectionReason => Resources.Strings.Reason(DirectionVerdict);

    public bool HasDirectionReason => DirectionReason is { Length: > 0 };

    public bool CanSwapDirection => Direction.CanSwap;

    private void ApplyDirection(TargetLanguage source, TargetLanguage target, bool chosen)
    {
        if (chosen)
        {
            IsSourceAuto = false;
        }

        SourceLanguage = source;
        Language = target;
    }

    /// <summary>
    /// What heads the source column of every row. The registry's name for the
    /// language the reader chose, and the generic label while the source is
    /// detected or unknown, because naming a language nobody chose would be a
    /// claim the app cannot make.
    /// </summary>
    public string EntrySourceHeading =>
        IsSourceAuto || SourceLanguage.IsUnknown
            ? Resources.Strings.EntrySourceLabel
            : SourceLanguage.Name.ToUpper(System.Globalization.CultureInfo.CurrentCulture);

    private void RefreshDirection()
    {
        Direction = TranslationDirection.Of(
            LanguageChoice.Of(SourceLanguage.Code, SourceLanguage.Name, SourceLanguage.Script),
            LanguageChoice.Of(Language.Code, Language.Name, Language.Script));

        DirectionVerdict = Guard.Inspect(Direction, ModelId, LanguageModelName);

        OnPropertyChanged(nameof(EntrySourceHeading));

        foreach (var entry in Entries)
        {
            entry.SourceHeading = EntrySourceHeading;
        }

        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(DirectionVerdict));
        OnPropertyChanged(nameof(DirectionReason));
        OnPropertyChanged(nameof(HasDirectionReason));
        OnPropertyChanged(nameof(CanSwapDirection));
        OnPropertyChanged(nameof(CanSend));
    }

    /// <summary>
    /// Runs detection over the text actually in the composer. Never over an
    /// empty buffer, never over a translation, and never over a source the
    /// reader chose. A detector that cannot tell leaves the source unknown,
    /// which the guard reports and the reader answers.
    /// </summary>
    public void DetectSourceLanguage(string text)
    {
        if (!IsSourceAuto || DetectSource is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var detected = DetectSource(text);

        if (detected.IsUnknown)
        {
            SourceLanguage = TargetLanguage.Unknown;
            return;
        }

        SourceLanguage = Languages.FirstOrDefault(l =>
                string.Equals(l.Code, detected.Code.Value, StringComparison.OrdinalIgnoreCase))
            ?? TargetLanguage.All.FirstOrDefault(l =>
                string.Equals(l.Code, detected.Code.Value, StringComparison.OrdinalIgnoreCase))
            ?? TargetLanguage.Unknown;
    }

    [ObservableProperty]
    public partial bool IsLanguagePickerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsSourcePickerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAttachMenuOpen { get; set; }

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// How many chips there are, said above the strip.
    ///
    /// The strip is capped and scrolls inside the cap, so past three rows the
    /// number is no longer countable by looking. This line sits outside the
    /// scroller and answers it without one.
    /// </summary>
    public string AttachmentSummary => Attachments.Count == 1
        ? "1 attached"
        : $"{Attachments.Count} attached";

    /// <summary>Rows in the Pinned section, filtered by the search text.</summary>
    public ICollectionView PinnedView { get; }

    /// <summary>Rows in the Chats section, filtered by the search text.</summary>
    public ICollectionView ChatsView { get; }

    [ObservableProperty]
    public partial ChatRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPinnedSectionExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsChatsSectionExpanded { get; set; } = true;

    /// <summary>Text in the composer before it is sent.</summary>
    [ObservableProperty]
    public partial string Draft { get; set; } = string.Empty;

    /// <summary>True while the chat name above the history is being edited.</summary>
    [ObservableProperty]
    public partial bool IsRenamingChat { get; set; }

    [ObservableProperty]
    public partial string ChatNameDraft { get; set; } = string.Empty;

    public bool HasChats => Rows.Count > 0;

    public bool HasSelection => SelectedRow is not null;

    public bool HasEntries => Entries.Count > 0;

    public int PinnedCount => Rows.Count(r => r.IsPinned);

    public int ChatsCount => Rows.Count(r => !r.IsPinned);

    /// <summary>
    /// Real translated-word count across the loaded chat, for the live figure
    /// on the Add to this project chat row.
    /// </summary>
    public int TranslatedWordCount =>
        Entries.Sum(e => Indexing.Chunking.TextChunker.CountWords(e.Source));

    public bool HasPinned => PinnedCount > 0;

    public string ChatHeaderStamp =>
        SelectedRow is null ? string.Empty : RelativeTime.Header(SelectedRow.Chat.UpdatedAt, _clock.Now);

    public bool CanSend =>
        !IsGenerating
        && DirectionVerdict.CanSend
        && (!string.IsNullOrWhiteSpace(Draft) || Attachments.Any(a => a.FilePath is not null));

    public EntryViewModel? ExpandedEntry => Entries.FirstOrDefault(e => e.IsExpanded);

    public bool HasExpandedEntry => ExpandedEntry is not null;

    [RelayCommand]
    private void CollapseExpandedEntry()
    {
        if (ExpandedEntry is { } open)
        {
            open.IsExpanded = false;
        }
    }

    private void WatchFold(EntryViewModel entry)
    {
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EntryViewModel.IsExpanded))
            {
                return;
            }

            if (entry.IsExpanded)
            {
                foreach (var other in Entries.Where(x => !ReferenceEquals(x, entry) && x.IsExpanded).ToList())
                {
                    other.IsExpanded = false;
                }
            }

            OnPropertyChanged(nameof(ExpandedEntry));
            OnPropertyChanged(nameof(HasExpandedEntry));
        };
    }

    public int MaxConcurrentFileJobs { get; init; } = 1;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var chats = await _store.GetChatsAsync(cancellationToken).ConfigureAwait(true);

        Rows.Clear();
        foreach (var chat in chats)
        {
            Rows.Add(new ChatRowViewModel(chat, _clock) { IsPinned = chat.IsPinned });
        }

        RaiseListCounts();

        if (Rows.Count > 0)
        {
            SelectedRow = Rows[0];
        }

        await EntriesLoaded.ConfigureAwait(true);
    }

    [RelayCommand]
    private void NewChat()
    {
        // A new chat is not stored until it holds a translation; this just
        // clears the pane and puts the caret in the composer.
        SelectedRow = null;
        Entries.Clear();
        Draft = string.Empty;
        OnPropertyChanged(nameof(HasEntries));
        ComposerFocusRequested?.Invoke();
    }

    /// <summary>Raised when focus should land in the composer.</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>
    /// The plus menu's File item. Multiselect is on and the filter is the
    /// formats a reader exists for.
    /// </summary>
    /// <summary>
    /// The formats a reader exists for. One list, so the picker's filter and
    /// what a drop accepts cannot drift apart.
    /// </summary>
    private static readonly string[] ReadableExtensions = [".pdf", ".docx", ".md", ".json", ".txt"];

    /// <summary>True while a drag carrying at least one readable file is over the chat.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    [RelayCommand]
    private async Task AttachFilesAsync()
    {
        IsAttachMenuOpen = false;

        var dialog = new OpenFileDialog
        {
            Title = "Attach files",
            Multiselect = true,
            Filter = "Documents (*.pdf;*.docx;*.md;*.json;*.txt)|*.pdf;*.docx;*.md;*.json;*.txt",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await AttachAsync(dialog.FileNames, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Which of these dropped paths this chat would take. A drag carrying
    /// nothing readable is not a drop target at all, so the cursor says no
    /// before the pointer is released rather than after.
    /// </summary>
    public static IReadOnlyList<string> Readable(IEnumerable<string>? paths) =>
        paths?.Where(p => ReadableExtensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
              .ToList()
        ?? [];

    /// <summary>
    /// Adds files as chips, from the picker or from a drop. The same content
    /// attached twice stays one chip: identity is the file's hash, not its
    /// name, so a copy saved under another name is still recognised. Hashing
    /// reads the file, which is why this is asynchronous and takes a token.
    /// </summary>
    public async Task AttachAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        foreach (var path in Readable(paths))
        {
            string hash;

            try
            {
                hash = await Indexing.ContentHash.OfFileAsync(path, cancellationToken).ConfigureAwait(true);
            }
            catch (System.IO.IOException)
            {
                // Unreadable right now, from a lock or a disconnected drive.
                // Attaching it unhashed is better than dropping it silently:
                // the reader will report the real problem when it runs.
                Attachments.Add(AttachmentViewModel.File(path));
                continue;
            }

            if (Attachments.Any(a => a.ContentHash == hash))
            {
                continue;
            }

            Attachments.Add(AttachmentViewModel.File(path, hash));
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentSummary));
    }

    /// <summary>The plus menu's Memory item. Project memory is always first.</summary>
    [RelayCommand]
    private void AttachMemory()
    {
        IsAttachMenuOpen = false;

        if (Attachments.Any(a => a.IsMemory))
        {
            return;
        }

        Attachments.Insert(0, AttachmentViewModel.Memory());
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(UsesMemory));
    }

    /// <summary>A chip removes only itself, which is why it is matched by id.</summary>
    [RelayCommand]
    private void RemoveAttachment(AttachmentViewModel attachment)
    {
        var match = Attachments.FirstOrDefault(a => a.Id == attachment.Id);
        if (match is not null)
        {
            Attachments.Remove(match);
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentSummary));
        OnPropertyChanged(nameof(UsesMemory));
    }

    [RelayCommand]
    private void ToggleAttachMenu() => IsAttachMenuOpen = !IsAttachMenuOpen;

    [RelayCommand]
    private void ToggleLanguagePicker()
    {
        IsLanguagePickerOpen = !IsLanguagePickerOpen;

        // Opened fresh every time. A search left over from last time hides most
        // of the list, and the reason would be a field the reader has to notice
        // before the list makes sense.
        if (IsLanguagePickerOpen)
        {
            LanguageSearch = string.Empty;
        }
    }

    [RelayCommand]
    private void ToggleSourcePicker()
    {
        IsSourcePickerOpen = !IsSourcePickerOpen;

        if (IsSourcePickerOpen)
        {
            SourceLanguageSearch = string.Empty;
        }
    }

    [RelayCommand]
    private void ChooseLanguage(TargetLanguage language)
    {
        if (!language.IsAvailable)
        {
            return;
        }

        ApplyDirection(SourceLanguage, language, chosen: false);
        IsLanguagePickerOpen = false;
        LanguageSearch = string.Empty;
    }

    [RelayCommand]
    private void ChooseSourceLanguage(TargetLanguage language)
    {
        if (!language.IsAvailable)
        {
            return;
        }

        ApplyDirection(language, Language, chosen: true);
        IsSourcePickerOpen = false;
        SourceLanguageSearch = string.Empty;
    }

    /// <summary>
    /// Both sides in one write. Swapping by setting one side and then the other
    /// leaves the pair briefly identical, which is a state the guard would report
    /// and the reader never asked for.
    /// </summary>
    [RelayCommand]
    private void SwapDirection()
    {
        if (!Direction.CanSwap)
        {
            return;
        }

        ApplyDirection(Language, SourceLanguage, chosen: true);
    }

    /// <summary>True for the row the composer is set to, so it can carry the tick.</summary>
    public bool IsCurrentLanguage(TargetLanguage language) => language.Code == Language.Code;

    partial void OnLanguageChanged(TargetLanguage value)
    {
        LanguagesView.Refresh();
        RefreshDirection();
    }

    partial void OnSourceLanguageChanged(TargetLanguage value)
    {
        SourceLanguagesView.Refresh();
        RefreshDirection();
    }

    [RelayCommand]
    private async Task PreviewFileAsync(AttachmentViewModel attachment)
    {
        if (attachment.FilePath is null)
        {
            return;
        }

        await Preview.OpenAsync(attachment.FilePath, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>
    /// Looks a phrase up in memory. Set by the shell; null before the index has
    /// been loaded.
    /// </summary>
    public Func<string, int, Indexing.Retrieval.MemoryLookup>? LookupMemory { get; set; }

    /// <summary>Raised so the activity panel can log what the lookup did.</summary>
    public Action<Indexing.Retrieval.MemoryLookup>? MemoryLookupCompleted { get; set; }

    /// <summary>
    /// Runs one translation. Null while no runtime is wired up, which is what
    /// leaves an entry stored with an empty result.
    ///
    /// The token is the entry's own: a send that supersedes an earlier one for
    /// the same entry cancels it through here rather than leaving it to finish
    /// into a region that has moved on.
    /// </summary>
    public Func<TranslationAsk, CancellationToken, Task<Runtime.Inference.TranslationOutcome>>? Translate { get; set; }

    /// <summary>
    /// Why the last send produced nothing. Read only when nothing came back: a
    /// refusal keeps the source, which is indistinguishable from a send that
    /// never happened unless the reason travels with it.
    /// </summary>
    public Func<string?>? LastVerdict { get; set; }

    /// <summary>
    /// Verifies a finished translation against its source, or null when no
    /// verifier is configured.
    ///
    /// It runs on the assembled pair rather than per unit, because the spans it
    /// produces are offsets into the text the entry renders. A unit is cut out
    /// of the message and its answer is spliced back over a byte range, so a
    /// span measured against one unit indexes the wrong part of the result. This
    /// is also the only place both halves of that pair exist at once: the units
    /// are gone by the time <see cref="Runtime.Inference.TranslationOutcome"/>
    /// is built.
    /// </summary>
    public Func<string, ContentTranslationResult, Core.Languages.TranslationDirection, Core.Verification.VerificationResult?>? Verify { get; set; }

    /// <summary>
    /// D6: names a chat from its first message, with the model that just
    /// translated it. Null until a runtime exists, and then the chat simply keeps
    /// the truncated name it was created with.
    /// </summary>
    public Func<ChatNameAsk, CancellationToken, Task<string?>>? NameChat { get; set; }

    /// <summary>
    /// True while the Advanced workspace is showing, which is the only place the
    /// per-entry and per-chat costs appear. Set by the shell.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowMetrics { get; set; }

    /// <summary>
    /// Tokens this chat has generated, across the entries that were actually
    /// run through a model. Entries answered from memory contribute nothing
    /// because nothing was generated for them.
    /// </summary>
    public int ChatTokenTotal => Entries.Sum(e => e.GeneratedTokens ?? 0);

    /// <summary>Total time the model spent on this chat.</summary>
    public int ChatDurationMs => Entries.Sum(e => e.DurationMs ?? 0);

    public bool HasChatMetrics => ChatTokenTotal > 0;

    /// <summary>
    /// "1 284 tokens", generated across this chat. Two cells rather than one
    /// joined string, so the separator the entry rows below dropped does not
    /// survive above them.
    /// </summary>
    public string ChatTokenFigure =>
        !HasChatMetrics
            ? string.Empty
            : string.Create(
                System.Globalization.CultureInfo.CurrentCulture,
                $"{ChatTokenTotal:N0} tokens");

    /// <summary>"31,2 s in this chat", the time beside it.</summary>
    public string ChatDurationFigure =>
        !HasChatMetrics
            ? string.Empty
            : $"{EntryViewModel.FormatDuration(ChatDurationMs)} in this chat";

    private void RaiseChatMetrics()
    {
        OnPropertyChanged(nameof(ChatTokenTotal));
        OnPropertyChanged(nameof(ChatDurationMs));
        OnPropertyChanged(nameof(HasChatMetrics));
        OnPropertyChanged(nameof(ChatTokenFigure));
        OnPropertyChanged(nameof(ChatDurationFigure));
    }

    /// <summary>
    /// Turns this chat's earlier translations and the indexed project into the
    /// block the model is given. Set by the shell, which owns the index; null
    /// before it has loaded, and then the chip simply adds nothing.
    /// </summary>
    public Func<string, IReadOnlyList<Indexing.Retrieval.TranslationPair>, string?>? BuildMemoryContext { get; set; }

    /// <summary>
    /// True while the Memory chip is on the composer. Nothing else turns
    /// context on: sends without it carry the text and nothing more, whichever
    /// chat they are in and however long that chat has run.
    /// </summary>
    public bool UsesMemory => Attachments.Any(a => a.IsMemory);

    /// <summary>
    /// The unsure threshold in force. Changing it re-marks every entry on
    /// screen; nothing is translated again.
    /// </summary>
    [ObservableProperty]
    public partial int UnsureThreshold { get; set; } = 80;

    partial void OnUnsureThresholdChanged(int value)
    {
        foreach (var entry in Entries)
        {
            entry.UnsureThreshold = value;
        }
    }

    partial void OnShowMetricsChanged(bool value)
    {
        foreach (var entry in Entries)
        {
            entry.ShowsFidelity = value;
        }
    }

    /// <summary>The word popover: which term is open, if any.</summary>
    [ObservableProperty]
    public partial MemorySegmentViewModel? OpenMemoryWord { get; set; }

    public bool IsMemoryWordOpen => OpenMemoryWord is not null;

    /// <summary>Text typed into the popover's own-word field.</summary>
    [ObservableProperty]
    public partial string OwnWord { get; set; } = string.Empty;

    /// <summary>True once the plus button has revealed the own-word field.</summary>
    [ObservableProperty]
    public partial bool IsOwnWordFieldOpen { get; set; }

    [RelayCommand]
    private void ShowMemoryWord(MemorySegmentViewModel segment)
    {
        OwnWord = string.Empty;
        IsOwnWordFieldOpen = false;
        OpenMemoryWord = segment;
    }

    [RelayCommand]
    private void CloseMemoryWord() => OpenMemoryWord = null;

    [RelayCommand]
    private void RevealOwnWordField() => IsOwnWordFieldOpen = true;

    /// <summary>Applies an alternative in one click.</summary>
    [RelayCommand]
    private void ApplyAlternative(string alternative)
    {
        ApplyWord(alternative);
    }

    /// <summary>Applies the user's own wording.</summary>
    [RelayCommand]
    private void UseOwnWord()
    {
        var word = OwnWord.Trim();
        if (word.Length > 0)
        {
            ApplyWord(word);
        }
    }

    /// <summary>
    /// Swaps the term in the entry that carries it. The result is rewritten in
    /// place, so the mark moves with it.
    /// </summary>
    private void ApplyWord(string replacement)
    {
        if (OpenMemoryWord?.Match is not { } match)
        {
            return;
        }

        var entry = Entries.FirstOrDefault(e => e.Matches.Contains(match));
        if (entry is not null && match.Start + match.Length <= entry.Result.Length)
        {
            entry.Result = entry.Result.Remove(match.Start, match.Length).Insert(match.Start, replacement);

            // The offsets after the swap no longer line up, so the marks are
            // dropped rather than left pointing at the wrong characters.
            entry.Matches = [];
        }

        OpenMemoryWord = null;
    }

    partial void OnOpenMemoryWordChanged(MemorySegmentViewModel? value) =>
        OnPropertyChanged(nameof(IsMemoryWordOpen));

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen)
        {
            SearchText = string.Empty;
        }
    }

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void TogglePinnedSection() => IsPinnedSectionExpanded = !IsPinnedSectionExpanded;

    [RelayCommand]
    private void ToggleChatsSection() => IsChatsSectionExpanded = !IsChatsSectionExpanded;

    [RelayCommand]
    private async Task TogglePinAsync(ChatRowViewModel row)
    {
        row.IsPinned = !row.IsPinned;
        await _store.SetPinnedAsync(row.Id, row.IsPinned, CancellationToken.None).ConfigureAwait(true);
        RefreshSections();
    }

    [RelayCommand]
    private void RenameFromRow(ChatRowViewModel row)
    {
        SelectedRow = row;
        BeginRenameChat();
    }

    [RelayCommand]
    private void BeginRenameChat()
    {
        if (SelectedRow is null)
        {
            return;
        }

        ChatNameDraft = SelectedRow.Name;
        IsRenamingChat = true;
    }

    /// <summary>Enter commits, and so does losing focus.</summary>
    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenamingChat || SelectedRow is null)
        {
            return;
        }

        IsRenamingChat = false;

        var name = ChatNameDraft.Trim();
        if (name.Length == 0 || name == SelectedRow.Name)
        {
            return;
        }

        SelectedRow.Name = name;
        SelectedRow.Chat.NameIsProvisional = false;

        await _store.RenameChatAsync(SelectedRow.Id, name, provisional: false, CancellationToken.None)
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelRename() => IsRenamingChat = false;

    [RelayCommand]
    private void ConfirmDeleteChat(ChatRowViewModel row) =>
        _showDialog(new ConfirmDialogViewModel(
            "Delete this chat?",
            $"{row.Name} and its translations are removed. This cannot be undone.",
            "Delete",
            "Cancel",
            // Both on the set's 16 box, so the two buttons carry glyphs of one
            // weight. IconCloseSmall is a 12 and sat small beside the bin.
            "IconDeleteBin",
            "IconCloseCrossSearch",
            () => _ = DeleteChatAsync(row),
            () => _showDialog(null)));

    private async Task DeleteChatAsync(ChatRowViewModel row)
    {
        if (_naming is { } naming && naming.Chat == row.Id)
        {
            await naming.Source.CancelAsync().ConfigureAwait(true);
        }

        await _store.DeleteChatAsync(row.Id, CancellationToken.None).ConfigureAwait(true);

        var wasSelected = SelectedRow == row;
        Rows.Remove(row);
        RefreshSections();

        // Deleting the chat you are reading lands on a new one, not on whichever
        // chat happens to sort first. Being dropped into an unrelated
        // conversation reads as if the wrong thing was deleted, and the next
        // thing typed would have gone into it. Deleting some other chat leaves
        // what you are reading alone.
        if (wasSelected)
        {
            NewChat();
        }
    }

    [RelayCommand]
    private void ConfirmDeleteEntry(EntryViewModel entry) =>
        _showDialog(new ConfirmDialogViewModel(
            "Delete this message?",
            "The source and its translation are removed. This cannot be undone.",
            "Delete",
            "Cancel",
            "IconDeleteBin",
            "IconCloseCrossSearch",
            () => _ = DeleteEntryAsync(entry),
            () => _showDialog(null)));

    private async Task DeleteEntryAsync(EntryViewModel entry)
    {
        // A load already in flight read the entries before this one went, and
        // would put it back when it finished. The send path waits for the same
        // reason.
        await EntriesLoaded.ConfigureAwait(true);

        // Whatever is out for this row is no longer wanted, and its answer must
        // not arrive to a row that has gone.
        entry.CancelRun();

        await _store.DeleteEntryAsync(entry.Entry.Id, CancellationToken.None).ConfigureAwait(true);

        Entries.Remove(entry);

        // The conversation's total is the sum of its rows, so it moves with them.
        RaiseChatMetrics();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        var files = Attachments.Where(a => a.FilePath is not null).ToList();

        if (text.Length == 0 && files.Count == 0)
        {
            return;
        }

        // A load that is still in flight would finish after this send and
        // replace the surface with what storage held before it, dropping the row
        // just added. Waiting for it costs nothing once a conversation is open
        // and is the only ordering in which both are correct.
        await EntriesLoaded.ConfigureAwait(true);

        if (Blocked(files))
        {
            return;
        }

        Draft = string.Empty;

        foreach (var file in files)
        {
            Attachments.Remove(file);
        }

        var now = DateTimeOffset.Now;

        // Read before StartChatAsync sets it. Nothing downstream can tell a first
        // entry from a later one, and only a first entry names its chat.
        var isNewChat = SelectedRow is null;
        var row = SelectedRow
            ?? await StartChatAsync(text.Length > 0 ? text : files[0].Label, now).ConfigureAwait(true);

        Entry? named = null;

        if (text.Length > 0)
        {
            named = await SendTextAsync(row, text, now).ConfigureAwait(true);
        }

        var queued = new List<(Entry Entry, EntryViewModel View)>();

        foreach (var file in files)
        {
            if (await AddFileEntryAsync(row, file, now).ConfigureAwait(true) is { } added)
            {
                queued.Add(added);
                named ??= added.Entry;
            }
        }

        foreach (var (entry, view) in queued)
        {
            await RunFileEntryAsync(entry, view).ConfigureAwait(true);
        }

        if (isNewChat && named is not null)
        {
            // Not awaited: this command's running state disables the send button,
            // and a chat name is not worth making anyone wait to type again.
            _ = NameNewChatSafelyAsync(row, named);
        }
    }

    private async Task<Entry> SendTextAsync(ChatRowViewModel row, string text, DateTimeOffset now)
    {
        // Ask memory what it knows about this wording, before the row is
        // written rather than after. The lexical path needs no model, so this
        // returns real matches from the indexed project even before a runtime
        // exists, and the activity panel can log the lookup either way.
        var lookup = LookupMemory?.Invoke(text, UnsureThreshold);

        var entry = new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = row.Id,
            Kind = LooksLikeSentence(text) ? EntryKind.Sentence : EntryKind.Words,
            Source = text,
            // Empty, and empty whatever memory found. Seeding this with the
            // source put untranslated text under the RESULT heading for the
            // whole of the wait -- and stored it there, so a reload showed it
            // again -- because the marks a lookup produces are measured against
            // the source and needed the source on screen to sit on. The region
            // renders the translation or it renders a placeholder; there is no
            // third thing it is allowed to show.
            Result = string.Empty,
            CreatedAt = now,
            State = EntryState.Pending,
            // Recorded now, because the result is headed with it and the
            // composer may be pointed somewhere else by the time it is read.
            TargetLanguage = Language.Name,
            // The guarded pair itself, which is what a retry runs. Taken from
            // Direction rather than from the two pickers separately, so the
            // row records the object the send was judged against.
            SourceCode = Direction.Source.Code.Value,
            TargetCode = Direction.Target.Code.Value,
        };

        await _store.AddEntryAsync(entry, CancellationToken.None).ConfigureAwait(true);

        row.Chat.UpdatedAt = now;
        row.Touched();

        var view = new EntryViewModel(entry) { UnsureThreshold = UnsureThreshold, ShowsFidelity = ShowMetrics, SourceHeading = EntrySourceHeading };

        if (lookup is not null)
        {
            view.Matches = lookup.Matches;
            MemoryLookupCompleted?.Invoke(lookup);
        }

        Entries.Add(view);

        Forget(row.Id);
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(ChatHeaderStamp));

        // The row is on screen and stored before the model is asked, so a slow
        // first load shows an entry translating rather than an empty composer.
        await TranslateEntryAsync(entry, view).ConfigureAwait(true);

        return entry;
    }

    /// <summary>
    /// The unawaited wrapper, and the only reason it exists is that nobody
    /// awaits it: a task with no awaiter has nobody to throw to, so anything
    /// escaping surfaces as an unhandled exception attributed to whatever thread
    /// happened to run the continuation. Measured -- the model runtime crashing
    /// mid-name arrived as an unhandled BetterRuntimeException with the
    /// translation already finished and on screen.
    ///
    /// A chat that could not be named keeps the name it has, which is what every
    /// other failure in here already does.
    /// </summary>
    private async Task NameNewChatSafelyAsync(ChatRowViewModel row, Entry entry)
    {
        try
        {
            await NameNewChatAsync(row, entry).ConfigureAwait(true);
        }
        catch (Exception)
        {
            row.Naming.Settle(() => { });
        }
    }

    /// <summary>How long the model gets to name a chat before the truncation stands.</summary>
    private static readonly TimeSpan NamingBudget = TimeSpan.FromSeconds(45);

    /// <summary>
    /// D6: the model that just translated the first message names the chat, in
    /// the language it translated into.
    ///
    /// Every failure is silent. A name is a convenience and the chat already has
    /// a usable one, so there is nothing here worth interrupting anyone with.
    /// </summary>
    private async Task NameNewChatAsync(ChatRowViewModel row, Entry entry)
    {
        // The placeholder went up when the row was created, so every exit from
        // here has to take it down again -- including the ones that name nothing,
        // where taking it down is what reveals the truncation as the fallback it
        // is.
        if (NameChat is null || !row.Chat.NameIsProvisional || entry.Result.Length == 0)
        {
            row.Naming.Settle(() => { });
            return;
        }

        string? title;

        using var budget = new CancellationTokenSource(NamingBudget);
        _naming = (row.Id, budget);

        if (!row.Naming.IsShowing)
        {
            row.Naming.BeginNow();
        }

        try
        {
            title = await NameChat(
                    new ChatNameAsk(entry.Source, DirectionFor(entry), row.Name),
                    budget.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            row.Naming.Settle(() => { });
            return;
        }
        finally
        {
            _naming = null;
        }

        // Asked again after the await. A rename typed while the model was working
        // is the reader's own choice and outranks it.
        if (string.IsNullOrWhiteSpace(title) || !row.Chat.NameIsProvisional)
        {
            row.Naming.Settle(() => { });
            return;
        }

        row.Chat.NameIsProvisional = false;

        await _store.RenameChatAsync(row.Id, title, provisional: false, CancellationToken.None)
            .ConfigureAwait(true);

        // Held back until the placeholder has had its floor, so a name that
        // arrives in eighty milliseconds does not flash a bar on the way past.
        row.Naming.Settle(() =>
        {
            row.Name = title;

            // The sidebar's filter reads Name and is not re-evaluated when a row
            // changes on its own, so a chat renamed under an active search would
            // stay on whichever side of the filter its old name put it.
            RefreshSections();
        });
    }

    /// <summary>The chat currently being named, so deleting it stops the attempt.</summary>
    private (Guid Chat, CancellationTokenSource Source)? _naming;

    public long MaxAttachmentBytes { get; init; } = 8L * 1024 * 1024;

    private bool Blocked(IReadOnlyList<AttachmentViewModel> files)
    {
        var stop = false;

        foreach (var file in files)
        {
            var problem = Unsendable(file.FilePath!);
            file.Problem = problem;
            stop |= problem is not null;
        }

        return stop;
    }

    private string? Unsendable(string path)
    {
        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That path cannot be read";
        }

        if (!File.Exists(full))
        {
            return "That file is no longer there";
        }

        if (!_readers.IsSupported(full))
        {
            var kind = Path.GetExtension(full);

            return kind.Length > 0
                ? $"{kind} files cannot be translated yet"
                : "Files with no extension cannot be translated";
        }

        var size = new FileInfo(full).Length;

        return size > MaxAttachmentBytes
            ? $"That file is {ByteSize.Format(size)}, over the {ByteSize.Format(MaxAttachmentBytes)} limit"
            : null;
    }

    private readonly Indexing.Readers.DocumentReaders _readers = new();

    private async Task<(Entry Entry, EntryViewModel View)?> AddFileEntryAsync(
        ChatRowViewModel row,
        AttachmentViewModel file,
        DateTimeOffset now)
    {
        if (Preview.TranslateDocument is null)
        {
            file.Problem = "No model is loaded";
            return null;
        }

        try
        {
            var full = Path.GetFullPath(file.FilePath!);

            var content = await _readers
                .ReadAsync(full, CancellationToken.None)
                .ConfigureAwait(true);

            var entry = new Entry
            {
                Id = Guid.NewGuid(),
                ChatId = row.Id,
                Kind = EntryKind.File,
                Source = content.Text,
                Result = string.Empty,
                CreatedAt = now,
                State = EntryState.Pending,
                TargetLanguage = Language.Name,
                SourceCode = Direction.Source.Code.Value,
                TargetCode = Direction.Target.Code.Value,
                FilePath = full,
                FileName = Path.GetFileName(full),
                FileSizeBytes = new FileInfo(full).Length,
            };

            await _store.AddEntryAsync(entry, CancellationToken.None).ConfigureAwait(true);

            row.Chat.UpdatedAt = now;
            row.Touched();

            var view = new EntryViewModel(entry) { UnsureThreshold = UnsureThreshold, ShowsFidelity = ShowMetrics, IsQueued = true, SourceHeading = EntrySourceHeading };
            Entries.Add(view);
            Forget(row.Id);
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(ChatHeaderStamp));

            return (entry, view);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            file.Problem = "That file could not be read";
            return null;
        }
    }

    private async Task RunFileEntryAsync(Entry entry, EntryViewModel view)
    {
        await _fileJobs.WaitAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            view.IsQueued = false;
            await TranslateEntryAsync(entry, view).ConfigureAwait(true);
        }
        finally
        {
            _fileJobs.Release();
        }
    }

    private readonly SemaphoreSlim _fileJobs = new(1, 1);

    /// <summary>
    /// D2: a failed entry offers Retry rather than a toast. The retry supersedes
    /// anything still in flight for that entry and clears the region first, so a
    /// late answer from the attempt it replaced cannot land on top of it.
    /// </summary>
    [RelayCommand]
    private Task RetryEntryAsync(EntryViewModel view) => RegenerateEntryAsync(view);

    private readonly SemaphoreSlim _regenerations = new(1, 1);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RegenerateEntryAsync(EntryViewModel view)
    {
        if (view is null || view.IsRegenerating)
        {
            return;
        }

        await EntriesLoaded.ConfigureAwait(true);

        view.BeginRegeneration();

        await _regenerations.WaitAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            if (!view.IsRegenerating)
            {
                return;
            }

            if (view.Kind == EntryKind.File)
            {
                await RunFileEntryAsync(view.Entry, view).ConfigureAwait(true);
            }
            else
            {
                await TranslateEntryAsync(view.Entry, view).ConfigureAwait(true);
            }
        }
        finally
        {
            _regenerations.Release();
        }
    }

    [RelayCommand]
    private void CancelRegeneration(EntryViewModel view) => view?.CancelRegeneration();

    [ObservableProperty]
    public partial string? RegenerateOfferModel { get; set; }

    [ObservableProperty]
    public partial bool IsRegeneratingAll { get; set; }

    public bool HasRegenerateOffer => RegenerateOfferModel is { Length: > 0 } && !IsRegeneratingAll;

    public string RegenerateOfferText =>
        $"{RegenerateOfferModel} is now the model. Translations below were made with the one before it.";

    partial void OnRegenerateOfferModelChanged(string? value) =>
        OnPropertyChanged(nameof(HasRegenerateOffer));

    partial void OnIsRegeneratingAllChanged(bool value) =>
        OnPropertyChanged(nameof(HasRegenerateOffer));

    public void OfferRegeneration(string modelName)
    {
        if (Entries.Any(entry => entry.HasResultText))
        {
            RegenerateOfferModel = modelName;
        }
    }

    [RelayCommand]
    private void DismissRegenerateOffer() => RegenerateOfferModel = null;

    [RelayCommand]
    private void ConfirmRegenerateAll()
    {
        var targets = Entries.Where(entry => entry.HasResultText && !entry.IsRegenerating).ToList();

        if (targets.Count == 0)
        {
            RegenerateOfferModel = null;
            return;
        }

        var model = RegenerateOfferModel;

        _showDialog(new ConfirmDialogViewModel(
            targets.Count == 1 ? "Regenerate 1 message?" : $"Regenerate {targets.Count} messages?",
            model is { Length: > 0 }
                ? $"Each one is sent again as it was first written, on {model}. They run one at a time, and every current translation stays until its replacement lands."
                : "Each one is sent again as it was first written. They run one at a time, and every current translation stays until its replacement lands.",
            "Regenerate",
            "Cancel",
            "IconRefresh",
            "IconCloseCrossSearch",
            () => RegenerationsCompleted = RegenerateAllAsync(targets),
            () => _showDialog(null)));
    }

    [RelayCommand]
    private void CancelRegenerateAll()
    {
        IsRegeneratingAll = false;

        foreach (var entry in Entries.Where(entry => entry.IsRegenerating))
        {
            entry.CancelRegeneration();
        }
    }

    public Task RegenerationsCompleted { get; private set; } = Task.CompletedTask;

    private async Task RegenerateAllAsync(IReadOnlyList<EntryViewModel> targets)
    {
        RegenerateOfferModel = null;
        IsRegeneratingAll = true;

        try
        {
            foreach (var view in targets)
            {
                if (!IsRegeneratingAll)
                {
                    return;
                }

                await RegenerateEntryAsync(view).ConfigureAwait(true);
            }
        }
        finally
        {
            IsRegeneratingAll = false;
        }
    }

    /// <summary>
    /// Where an exported file is written. Set by the shell to the save dialog,
    /// and by a test to a path it chose, so nothing opens a window to be
    /// exercised. Null back means the reader cancelled.
    /// </summary>
    public Func<string, string?>? PickSavePath { get; set; }

    [RelayCommand]
    private void ExportSource(EntryViewModel view) => Export(view, view.Source, view.FileName ?? string.Empty);

    [RelayCommand]
    private void ExportResult(EntryViewModel view) => Export(view, view.Result, view.ExportFileName);

    private void Export(EntryViewModel view, string text, string suggested)
    {
        if (text.Length == 0 || suggested.Length == 0)
        {
            return;
        }

        var chosen = (PickSavePath ?? SaveDialog)(suggested);

        if (string.IsNullOrEmpty(chosen))
        {
            return;
        }

        try
        {
            File.WriteAllText(chosen, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            view.Note = ex.Message;
        }
    }

    private static string? SaveDialog(string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Title = Resources.Strings.EntryExportTitle,
            FileName = suggested,
            Filter = Resources.Strings.EntryExportFilter,
            AddExtension = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Runs the entry through the local runtime and stores what comes back.
    /// Nothing here throws into the send path: a runtime that will not start
    /// leaves the entry with no translation, which is what the state means.
    /// </summary>
    private async Task TranslateEntryAsync(Entry entry, EntryViewModel view)
    {
        if (Translate is null)
        {
            view.Idle();
            return;
        }

        // The row's own pair, judged before anything is sent. A row whose
        // recorded language no longer resolves has no target to translate into,
        // and the engine would not stop it: same-language is the only pair it
        // refuses, and an unknown language is not the same as anything. Without
        // this the model is asked to translate into nothing and told not to
        // return the source, and whatever comes back is stored as the answer.
        var pair = Guard.Inspect(DirectionFor(entry), ModelId, LanguageModelName);

        if (!pair.CanSend)
        {
            view.Fail(Resources.Strings.Reason(pair));
            return;
        }

        var replacing = view.IsRegenerating && view.HasResultText;

        var job = Runtime.Agents.JobRegistry.Shared.Track("chat", 0);
        _liveJobs[entry.Id] = job;
        view.AttachJob(job);
        job.Running();

        var (sequence, token) = view.BeginRun(job.Token);
        RaiseGenerationState();

        string? note = null;

        try
        {
            // Routing, cutting and reassembly belong to the engine, and the same
            // ones answer an agent. What stays here is what only a chat knows:
            // which memory travels with each unit, and what one unit cost.
            var direction = DirectionFor(entry);
            var tokens = 0;
            var elapsed = TimeSpan.Zero;

            var content = await ContentTranslation.TranslateAsync(
                entry.Source,
                async (text, unitToken) =>
                {
                    await job.WaitWhilePausedAsync(unitToken).ConfigureAwait(true);

                    var unit = await Translate(
                        new TranslationAsk(text, direction, MemoryFor(text, view), UsesMemory),
                        unitToken)
                        .ConfigureAwait(true);

                    tokens += unit.GeneratedTokens;
                    elapsed += unit.Duration;

                    return unit.Text;
                },
                token)
                .ConfigureAwait(true);

            note = content.Refusal ?? content.Note;

            // Verified here, on the assembled answer, because this is where the
            // pair the reader sees finally exists. The per-unit outcomes carry a
            // token count and a duration and nothing else survives them, so a
            // verification attached to one of them would be discarded with it.
            var outcome = content.Refusal is not null
                ? Runtime.Inference.TranslationOutcome.None(elapsed)
                : new Runtime.Inference.TranslationOutcome(content.Text, tokens, elapsed)
                {
                    Verification = content.Text is { Length: > 0 }
                        ? Verify?.Invoke(entry.Source, content, direction)
                        : null,
                }.CountingReverseCalls();

            // A run this entry no longer owns: something superseded it while it
            // was out. Its answer is stale by definition, and the runtime
            // reports a cancelled generation as an ordinary empty outcome, so
            // writing it would land a failure on the run that replaced it.
            if (!view.Owns(sequence))
            {
                return;
            }

            // Recorded even when nothing came back: a failure that took forty
            // seconds is a different fact from one that failed at once, and both
            // are worth having in Advanced.
            view.GeneratedTokens = outcome.GeneratedTokens > 0 ? outcome.GeneratedTokens : null;
            view.DurationMs = (int)outcome.Duration.TotalMilliseconds;
            RaiseChatMetrics();

            view.Verification = outcome.Verification;
            view.WasStopped = content.Stopped;

            if (outcome.HasText)
            {
                view.Complete(outcome.Text!, content.Stopped ? view.StoppedLabel : note);
            }
            else
            {
                // No text and no reason reads as an application that did
                // nothing. Saying which gate declined it is the difference
                // between "the app is broken" and "the glossary wanted a
                // different word".
                view.Fail(content.Stopped ? StoppedWithNothing : note ?? LastVerdict?.Invoke());
            }

            // Written from the outcome rather than from the view: the view holds
            // its answer back for as long as the placeholder is still owed, and
            // the store is not waiting on a presentation delay.
            if (!replacing || outcome.HasText)
            {
                entry.Result = outcome.Text ?? string.Empty;
                entry.State = outcome.HasText
                    ? content.Stopped ? EntryState.Stopped : EntryState.Done
                    : EntryState.Failed;

                await _store.UpdateEntryResultAsync(entry, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or stopped. Whatever replaced it owns the region now,
            // and has already cleared it.
        }
        finally
        {
            if (_liveJobs.TryGetValue(entry.Id, out var finished) && ReferenceEquals(finished, job))
            {
                _liveJobs.Remove(entry.Id);
            }

            if (job.Token.IsCancellationRequested)
            {
                job.Stopped();
            }
            else
            {
                job.Completed();
            }

            Runtime.Agents.JobRegistry.Shared.Forget(job.Id);
            RaiseGenerationState();
        }
    }

    private const string StoppedWithNothing = "Stopped before anything came back.";

    private readonly Dictionary<Guid, Runtime.Agents.TrackedJob> _liveJobs = [];

    public bool IsGenerating => _liveJobs.Count > 0;

    public IReadOnlyList<string> LiveJobIds => [.. _liveJobs.Values.Select(job => job.Id)];

    private void RaiseGenerationState()
    {
        OnPropertyChanged(nameof(IsGenerating));
        OnPropertyChanged(nameof(CanSend));
        StopGenerationCommand.NotifyCanExecuteChanged();
    }

    private void Reattach(EntryViewModel view)
    {
        if (_liveJobs.TryGetValue(view.Entry.Id, out var job))
        {
            view.AttachJob(job);
        }
    }

    [RelayCommand(CanExecute = nameof(IsGenerating))]
    private void StopGeneration()
    {
        foreach (var entry in Entries.Where(entry => entry.IsRunning).ToList())
        {
            StopEntry(entry);
        }

        foreach (var job in _liveJobs.Values.ToList())
        {
            job.Cancel();
        }
    }

    [RelayCommand]
    private void StopEntry(EntryViewModel view)
    {
        if (view is null)
        {
            return;
        }

        if (view.IsRegenerating)
        {
            view.CancelRegeneration();
            return;
        }

        view.Stop();
    }

    [RelayCommand]
    private void PauseEntry(EntryViewModel view) => view?.Pause();

    [RelayCommand]
    private void ResumeEntry(EntryViewModel view) => view?.Resume();


    /// <summary>
    /// One side of an entry's recorded pair, rebuilt from the code it was sent
    /// under. The registry is asked for the name and script only -- the code is
    /// the identity and is never reconsidered -- so a language the picker no
    /// longer offers still resolves to itself.
    ///
    /// An unrecorded or unknown code answers <see cref="LanguageChoice.Unknown"/>
    /// rather than a substitute. The guard turns that into a refusal the reader
    /// can see, which is the only honest answer: the alternative is running the
    /// retry in a language nobody chose, under the heading the row already has.
    /// </summary>
    private static LanguageChoice Recorded(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return LanguageChoice.Unknown;
        }

        var entry = new LanguageRegistry().Resolve(code);

        return entry is null
            ? LanguageChoice.Unknown
            : LanguageChoice.Of(entry.Code, entry.Name, entry.Script);
    }

    /// <summary>
    /// The pair one entry is retried under, which is the pair it was sent under
    /// and not the one the pickers are showing now. Both sides come off the row.
    ///
    /// Two fallbacks, both for rows written before the codes were recorded: the
    /// target is resolved from the stored English name, and the source -- which
    /// those rows never carried at all -- is taken from the composer. Neither
    /// applies to a row this build wrote, and neither can substitute a language
    /// for one that fails to resolve.
    /// </summary>
    internal TranslationDirection DirectionFor(Entry entry)
    {
        var target = entry.TargetCode.Length > 0
            ? Recorded(entry.TargetCode)
            : Recorded(new LanguageRegistry().Resolve(entry.TargetLanguage)?.Code ?? string.Empty);

        var source = entry.SourceCode.Length > 0
            ? Recorded(entry.SourceCode)
            : LanguageChoice.Of(SourceLanguage.Code, SourceLanguage.Name, SourceLanguage.Script);

        return TranslationDirection.Of(source, target);
    }

    /// <summary>
    /// The remembered material for one send, or null. Null is not a failure and
    /// not an empty string: it is what every send without the chip returns, and
    /// what keeps the model's context free.
    /// </summary>
    private string? MemoryFor(string text, EntryViewModel current) =>
        UsesMemory ? BuildMemoryContext?.Invoke(text, PriorPairs(current)) : null;

    /// <summary>
    /// What this chat has already translated. An entry whose result is still its
    /// own source has not been translated, and a pair of one phrase with itself
    /// would teach the model to leave the text in English. Nothing writes such a
    /// pair any more; the guard stays for the rows that were stored before that
    /// was true.
    /// </summary>
    private IReadOnlyList<Indexing.Retrieval.TranslationPair> PriorPairs(EntryViewModel current) =>
    [
        .. Entries
            .Where(e => !ReferenceEquals(e, current))
            .Where(e => e.Result.Length > 0 && !string.Equals(e.Source, e.Result, StringComparison.Ordinal))
            .Select(e => new Indexing.Retrieval.TranslationPair(e.Source, e.Result))
    ];

    private async Task<ChatRowViewModel> StartChatAsync(string firstEntryText, DateTimeOffset now)
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = ProvisionalName(firstEntryText),
            CreatedAt = now,
            UpdatedAt = now,
            NameIsProvisional = true,
        };

        await _store.AddChatAsync(chat, CancellationToken.None).ConfigureAwait(true);

        var row = new ChatRowViewModel(chat, _clock);

        // Straight away, not when the naming call starts. The name is asked for
        // only after the translation comes back, so the truncated source would
        // otherwise sit in the sidebar for the whole of that wait looking like
        // the chat's settled name -- and then change under the reader.
        if (NameChat is not null)
        {
            row.Naming.BeginNow();
        }

        Rows.Insert(0, row);
        RefreshSections();

        // Selecting it starts a load, and a chat created a moment ago has
        // nothing stored to load. Saying so here keeps the read off the wire and
        // closes the window in which that read could land after the send had
        // already put its row on the surface and wipe it.
        _createdEmpty = row.Id;

        SelectedRow = row;
        return row;
    }

    /// <summary>
    /// D6, unconfirmed: the name a chat carries until a model can summarize it.
    /// </summary>
    public static string ProvisionalName(string text) => ChatWriter.ProvisionalName(text);

    private static bool LooksLikeSentence(string text) => ChatWriter.KindFor(text) == EntryKind.Sentence;

    private ICollectionView BuildView(bool pinned)
    {
        var view = new CollectionViewSource { Source = Rows }.View;
        view.Filter = item => item is ChatRowViewModel row
            && row.IsPinned == pinned
            && MatchesSearch(row);
        return view;
    }

    private bool MatchesSearch(ChatRowViewModel row) =>
        string.IsNullOrWhiteSpace(SearchText)
        || row.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private void RefreshSections()
    {
        PinnedView.Refresh();
        ChatsView.Refresh();
        RaiseListCounts();
    }

    private void RaiseListCounts()
    {
        OnPropertyChanged(nameof(HasChats));
        OnPropertyChanged(nameof(PinnedCount));
        OnPropertyChanged(nameof(ChatsCount));
        OnPropertyChanged(nameof(HasPinned));
    }

    partial void OnSearchTextChanged(string value) => RefreshSections();

    partial void OnDraftChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSelectedRowChanged(ChatRowViewModel? value)
    {
        // One row across both lists carries the mark. The lists cannot work it
        // out between themselves: each holds a selection of its own, and the
        // one that does not own the open chat would go on showing its last.
        foreach (var row in Rows)
        {
            row.IsCurrent = ReferenceEquals(row, value);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ChatHeaderStamp));
        IsRenamingChat = false;

        EntriesLoaded = LoadEntriesAsync(value);
    }

    /// <summary>
    /// The entry load now in flight, or a completed task when none is. The load
    /// stopped finishing inline the moment it moved off the UI thread, so
    /// anything that means "the surface is ready" has something to await.
    /// </summary>
    public Task EntriesLoaded { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _loading;

    /// <summary>A chat made in this session that storage cannot yet have rows for.</summary>
    private Guid? _createdEmpty;

    /// <summary>
    /// Reads one conversation onto the surface, and abandons the one before it.
    ///
    /// The token is what makes a burst of clicks cost one conversation rather
    /// than the sum of them. It is checked before each view model as well as
    /// around the read, because building them is the expensive half: a load that
    /// has been replaced must stop building, not merely stop reading.
    /// </summary>
    private async Task LoadEntriesAsync(ChatRowViewModel? row)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;

        if (row is null)
        {
            Entries.Clear();
            OnPropertyChanged(nameof(HasEntries));
            return;
        }

        // Cleared on every load, so a chat that was empty when it was made still
        // reads from storage the next time it is opened.
        var fresh = _createdEmpty == row.Id;
        _createdEmpty = null;

        if (fresh)
        {
            Entries.Clear();
            OnPropertyChanged(nameof(HasEntries));
            RaiseChatMetrics();
            return;
        }

        if (_cached.TryGetValue(row.Id, out var remembered))
        {
            Keep(row.Id);
            Publish(remembered);
            return;
        }

        var loading = new CancellationTokenSource();
        _loading = loading;

        var token = loading.Token;
        var heading = EntrySourceHeading;
        IReadOnlyList<EntryViewModel> built;

        try
        {
            var stored = await Task.Run(
                () => _store.GetEntriesAsync(row.Id, token),
                token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            // The newest page first. Deciding whether each entry is Markdown or
            // JSON is what building one costs, and paying it for a conversation
            // of several hundred before anything is on screen is the wait this
            // splits. The reader sees the end of the conversation, which is what
            // they opened it for, while the rest is still being built.
            var from = Math.Max(0, stored.Count - FirstPage);

            if (from > 0)
            {
                var page = await BuildAsync(stored, from, stored.Count, heading, token).ConfigureAwait(true);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Publish(page);
            }

            built = await BuildAsync(stored, 0, stored.Count, heading, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        Remember(row.Id, built);

        Publish(built);
    }

    private const int FirstPage = 40;

    private const int ChatsKept = 4;

    private readonly Dictionary<Guid, IReadOnlyList<EntryViewModel>> _cached = [];

    private readonly LinkedList<Guid> _keptOrder = new();

    private static Task<IReadOnlyList<EntryViewModel>> BuildAsync(
        IReadOnlyList<Entry> stored,
        int from,
        int to,
        string heading,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var models = new List<EntryViewModel>(to - from);

                for (var i = from; i < to; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    models.Add(new EntryViewModel(stored[i]) { SourceHeading = heading });
                }

                return (IReadOnlyList<EntryViewModel>)models;
            },
            cancellationToken);

    /// <summary>
    /// Emptied and refilled together. Clearing up front left the surface blank
    /// for as long as the read took, and a load that had been replaced could
    /// still wipe a row the composer had just added to the chat that replaced it.
    /// </summary>
    private void Publish(IReadOnlyList<EntryViewModel> entries)
    {
        Entries.Clear();

        foreach (var entry in entries)
        {
            // Applied here rather than where the rows are built: that runs on a
            // pool thread for a chat the reader may already have left, and the
            // workspace settings a row renders from belong to the surface it
            // lands on.
            entry.UnsureThreshold = UnsureThreshold;
            entry.ShowsFidelity = ShowMetrics;

            Reattach(entry);

            Entries.Add(entry);
        }

        OnPropertyChanged(nameof(HasEntries));
        RaiseChatMetrics();
    }

    /// <summary>
    /// Keeps the last few conversations read, so going back to the one just left
    /// paints without touching storage. The view models are the same objects the
    /// surface held, so a result written into one afterwards is already there
    /// when the conversation is opened again; only adding or removing a row makes
    /// the list itself stale, and those sites forget it.
    /// </summary>
    private void Remember(Guid chat, IReadOnlyList<EntryViewModel> entries)
    {
        _cached[chat] = entries;
        Keep(chat);

        while (_keptOrder.Count > ChatsKept)
        {
            var oldest = _keptOrder.Last!.Value;

            _keptOrder.RemoveLast();
            _cached.Remove(oldest);
        }
    }

    private void Keep(Guid chat)
    {
        _keptOrder.Remove(chat);
        _keptOrder.AddFirst(chat);
    }

    private void Forget(Guid chat)
    {
        _cached.Remove(chat);
        _keptOrder.Remove(chat);
    }

    /// <summary>
    /// Something other than this window wrote a row into a chat. Nothing else
    /// would notice: the list is read once at startup and the entries of a chat
    /// are cached behind an invariant that every writer forgets its own chat --
    /// which an agent in another process cannot do.
    ///
    /// The reader's place is kept. A chat they are not looking at is only
    /// forgotten, so it re-reads when they next open it; the open one is
    /// reloaded where it stands. A chat that is new to this window is added to
    /// the list rather than the whole list being re-read, because re-reading it
    /// reselects the first row.
    /// </summary>
    public async Task NoticeEntryAsync(Guid chatId)
    {
        Forget(chatId);

        var known = Rows.FirstOrDefault(r => r.Chat.Id == chatId);

        if (known is null)
        {
            var chats = await _store.GetChatsAsync(CancellationToken.None).ConfigureAwait(true);
            var written = chats.FirstOrDefault(c => c.Id == chatId);

            if (written is null)
            {
                return;
            }

            Rows.Insert(0, new ChatRowViewModel(written, _clock));
            RefreshSections();
            return;
        }

        known.Chat.UpdatedAt = DateTimeOffset.Now;
        known.Touched();
        RefreshSections();

        if (SelectedRow?.Chat.Id == chatId)
        {
            await LoadEntriesAsync(known).ConfigureAwait(true);
        }
    }
}
