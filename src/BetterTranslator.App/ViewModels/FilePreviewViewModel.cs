using System.IO;
using BetterTranslator.Indexing.Markdown;
using BetterTranslator.Indexing.Readers;
using BetterTranslator.Runtime.Inference;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// S9. Opens beside the chat and renders real file content.
///
/// The version dropdown SELECTS which version to show and never starts a run.
/// Translating a document costs a model call per paragraph and minutes on a long
/// file, so it is its own action with its own button -- a dropdown that silently
/// began that would be indistinguishable from one that had frozen.
/// </summary>
public sealed partial class FilePreviewViewModel : ObservableObject
{
    private readonly DocumentReaders _readers = new();

    private CancellationTokenSource? _running;

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DocumentContent? Content { get; set; }

    /// <summary>Formatted when true, the literal source when false. Markdown only.</summary>
    [ObservableProperty]
    public partial bool IsFormatted { get; set; } = true;

    /// <summary>
    /// Source or Translate. Selecting a version never starts a run.
    ///
    /// Opens on Translate, because that is what a reader opened the file for.
    /// Before a run there is nothing translated to show, so the panel falls back
    /// to the source with the notice beside it saying exactly that.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowTranslated { get; set; } = true;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// True once a translated version of this file exists. Nothing does yet,
    /// so the header carries the untranslated notice.
    /// </summary>
    [ObservableProperty]
    public partial bool HasTranslation { get; set; }

    /// <summary>The translated document, once one exists. Null until then.</summary>
    [ObservableProperty]
    public partial string? TranslatedText { get; set; }

    [ObservableProperty]
    public partial bool IsTranslating { get; set; }

    /// <summary>
    /// What the run is doing, in the header. A document pass is many model calls
    /// and a silent one looks like a hang.
    /// </summary>
    [ObservableProperty]
    public partial string? Activity { get; set; }

    [ObservableProperty]
    public partial int Percent { get; set; }

    /// <summary>
    /// What the run produced, said plainly: how many lines were translated, how
    /// many kept their source, and why. A document that came back with a third of
    /// its lines in English and no explanation is the failure this reports
    /// against.
    /// </summary>
    [ObservableProperty]
    public partial string? Summary { get; set; }

    /// <summary>
    /// Runs a whole document, keeping its line count. Set by the shell; null
    /// until a runtime exists, and the button is unavailable while it is.
    /// </summary>
    public Func<string, IProgress<DocumentProgress>, CancellationToken, Task<DocumentTranslation>>?
        TranslateDocument { get; set; }

    public bool CanTranslate => TranslateDocument is not null && !IsTranslating && Content is not null;

    /// <summary>
    /// The single line under the toolbar: what the run is doing while it runs,
    /// and what it produced once it stops. One property rather than two, because
    /// the two never appear together and a row that swapped between them would
    /// move the content below it.
    /// </summary>
    public string RunNote => Activity ?? Summary ?? string.Empty;

    public bool HasRunNote => RunNote.Length > 0;

    /// <summary>
    /// Parsed once per translated document rather than on every property change:
    /// Blocks is read by the ItemsControl on any view flag moving, and reparsing a
    /// hundred kilobytes of Markdown on each of those is a visible stall.
    /// </summary>
    private IReadOnlyList<MdBlock>? _translatedBlocks;

    /// <summary>
    /// The formatted view of whichever version is selected.
    ///
    /// It used to return the source's blocks unconditionally, so a translated
    /// Markdown file rendered formatted came back in English while the Source
    /// toggle beside it showed the Czech. The version picker chooses the version
    /// in both views or it chooses nothing.
    /// </summary>
    public IReadOnlyList<MdBlock> Blocks
    {
        get
        {
            if (!ShowTranslated || !HasRenderingSwitch || TranslatedText is not { Length: > 0 } translated)
            {
                return Content?.Blocks ?? [];
            }

            return _translatedBlocks ??= MarkdownBlockParser.Parse(translated);
        }
    }

    /// <summary>PDF, DOCX, JSON and TXT show one view and no switch.</summary>
    public bool HasRenderingSwitch => Content?.HasRenderingSwitch ?? false;

    // Which version is shown and how it is rendered are two questions. A
    // markdown file has a source and a formatted view whichever version of it
    // is on screen, so picking Translate must not throw the reader back to
    // raw text.
    public bool ShowFormatted => HasRenderingSwitch && IsFormatted;

    public bool ShowSourceText => !ShowFormatted;

    public bool IsMonospace => Content?.IsMonospace ?? false;

    /// <summary>
    /// The text on screen. Falls back to the source while no translation exists,
    /// which is what the notice beside it says out loud.
    /// </summary>
    public string SourceText =>
        (ShowTranslated ? TranslatedText : null) ?? Content?.Text ?? string.Empty;

    /// <summary>
    /// Stated verbatim while the file has no translated version, so choosing
    /// Translated cannot look like it will produce one.
    /// </summary>
    public string VersionNotice => HasTranslation ? string.Empty : "Source shown until you run it";

    public bool ShowVersionNotice => !HasTranslation;

    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        // A run belonging to the file being closed must not write its answer over
        // the one being opened.
        CancelRun();

        FilePath = path;
        FileName = Path.GetFileName(path);
        IsFormatted = true;
        ShowTranslated = true;
        HasTranslation = false;
        TranslatedText = null;
        _translatedBlocks = null;
        Summary = null;
        Activity = null;
        Percent = 0;
        IsOpen = true;

        // Reading happens off the UI thread inside the readers.
        Content = await _readers.ReadAsync(path, cancellationToken).ConfigureAwait(true);
    }

    [ObservableProperty]
    public partial bool IsVersionMenuOpen { get; set; }

    public string VersionLabel => ShowTranslated ? "Translate" : "Source";

    [RelayCommand]
    private void Close()
    {
        CancelRun();
        IsOpen = false;
    }

    /// <summary>
    /// Translates the whole document, keeping its line count.
    ///
    /// The result is shown rather than written: this produces a translated view
    /// of the file, and overwriting somebody's document is a different action
    /// that has to be asked for on purpose.
    ///
    /// No longer reachable from the panel, which is a viewer. A file is
    /// translated by sending it from the composer, so there is one way in rather
    /// than a button here and a send there.
    /// </summary>
    public async Task TranslateAsync()
    {
        if (TranslateDocument is null || Content is null || IsTranslating)
        {
            return;
        }

        CancelRun();

        var running = new CancellationTokenSource();
        _running = running;

        IsTranslating = true;
        Summary = null;
        Percent = 0;
        Activity = "Starting";
        ShowTranslated = true;

        try
        {
            var progress = new Progress<DocumentProgress>(report =>
            {
                // Reports are posted, so one can still be waiting when the run
                // ends. Applied after that, it would overwrite the summary with a
                // stale activity line and leave the header describing work that
                // has already finished.
                if (!IsTranslating)
                {
                    return;
                }

                Activity = report.Activity;
                Percent = report.Percent;
            });

            var result = await TranslateDocument(Content.Text, progress, running.Token).ConfigureAwait(true);

            // A cancelled run still returns a whole document, but it is a partial
            // translation and must not be presented as the file's translation.
            if (result.WasCancelled)
            {
                ShowTranslated = false;
                Summary = "Stopped. Nothing was changed.";
                return;
            }

            TranslatedText = result.Text;
            HasTranslation = true;
            Summary = Describe(result);
        }
        catch (OperationCanceledException)
        {
            ShowTranslated = false;
            Summary = "Stopped. Nothing was changed.";
        }
        finally
        {
            IsTranslating = false;
            Activity = null;
            _running = null;
            running.Dispose();
        }
    }

    [RelayCommand]
    private void StopTranslating() => CancelRun();

    private void CancelRun()
    {
        try
        {
            _running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished; there is nothing to stop.
        }
    }

    /// <summary>
    /// One line of what actually happened. Counts only -- a summary that quoted
    /// the document would become a listing of it.
    /// </summary>
    private static string Describe(DocumentTranslation result)
    {
        var parts = new List<string> { $"{result.LinesTranslated} of {result.LinesTotal} lines translated" };

        if (result.LinesKept > 0)
        {
            parts.Add($"{result.LinesKept} kept their source");
        }

        if (result.ParagraphsJoined > 0)
        {
            parts.Add($"{result.ParagraphsJoined} wrapped paragraph{(result.ParagraphsJoined == 1 ? "" : "s")} joined");
        }

        if (result.SpacesRepaired + result.FragmentsRepaired + result.LinesRetranslated > 0)
        {
            parts.Add($"{result.SpacesRepaired + result.FragmentsRepaired + result.LinesRetranslated} repaired");
        }

        return string.Join(", ", parts) + ".";
    }

    [RelayCommand]
    private void ToggleVersionMenu() => IsVersionMenuOpen = !IsVersionMenuOpen;

    [RelayCommand]
    private void ShowSourceVersion()
    {
        ShowTranslated = false;
        IsVersionMenuOpen = false;
    }

    /// <summary>
    /// Selects the translated version to display. It never starts a run: while no
    /// translation exists the header says so and the source stays on screen. The
    /// run is the Translate button, because it costs minutes and a dropdown that
    /// silently began one would be indistinguishable from a frozen window.
    /// </summary>
    [RelayCommand]
    private void ShowTranslatedVersion()
    {
        ShowTranslated = true;
        IsVersionMenuOpen = false;
    }

    [RelayCommand]
    private void ShowFormattedView() => IsFormatted = true;

    [RelayCommand]
    private void ShowSourceView() => IsFormatted = false;

    partial void OnContentChanged(DocumentContent? value) => RaiseViewFlags();

    partial void OnIsFormattedChanged(bool value) => RaiseViewFlags();

    partial void OnShowTranslatedChanged(bool value)
    {
        OnPropertyChanged(nameof(VersionLabel));
        RaiseViewFlags();
    }

    partial void OnTranslatedTextChanged(string? value)
    {
        _translatedBlocks = null;
        RaiseViewFlags();
    }

    partial void OnIsTranslatingChanged(bool value) => OnPropertyChanged(nameof(CanTranslate));

    partial void OnActivityChanged(string? value) => RaiseRunNote();

    partial void OnSummaryChanged(string? value) => RaiseRunNote();

    private void RaiseRunNote()
    {
        OnPropertyChanged(nameof(RunNote));
        OnPropertyChanged(nameof(HasRunNote));
    }

    partial void OnHasTranslationChanged(bool value)
    {
        OnPropertyChanged(nameof(VersionNotice));
        OnPropertyChanged(nameof(ShowVersionNotice));
    }

    private void RaiseViewFlags()
    {
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(HasRenderingSwitch));
        OnPropertyChanged(nameof(ShowFormatted));
        OnPropertyChanged(nameof(ShowSourceText));
        OnPropertyChanged(nameof(IsMonospace));
        OnPropertyChanged(nameof(SourceText));
    }
}
