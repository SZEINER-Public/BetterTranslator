using System.IO;
using BetterTranslator.Indexing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BetterTranslator.App.ViewModels;

/// <summary>
/// S11. Five multi-select rows; the action button names what it will actually
/// do, and the two chat rows are gated on there being a chat at all.
/// </summary>
public sealed partial class AddSourcesViewModel : ObservableObject
{
    private readonly Func<IndexingRequest, Task> _startIndexing;
    private readonly Action _close;

    public AddSourcesViewModel(
        bool anythingIndexed,
        string? projectName,
        int chatCount,
        int translatedWords,
        Func<IndexingRequest, Task> startIndexing,
        Action close)
    {
        _startIndexing = startIndexing;
        _close = close;

        AnythingIndexed = anythingIndexed;
        ProjectName = projectName;
        ChatCount = chatCount;
        TranslatedWords = translatedWords;
    }

    public bool AnythingIndexed { get; }

    public string? ProjectName { get; }

    public int ChatCount { get; }

    public int TranslatedWords { get; }

    /// <summary>
    /// Names the project once something is indexed, so the subhead matches the
    /// hub on the map.
    /// </summary>
    public string Subhead => AnythingIndexed && ProjectName is not null
        ? $"Already indexing {ProjectName}. Anything you add is indexed alongside it."
        : "Nothing is indexed yet. Pick what memory should learn from - it all stays on this machine.";

    // ---- row 1, a folder ----

    [ObservableProperty]
    public partial bool FolderSelected { get; set; }

    [ObservableProperty]
    public partial string? FolderPath { get; set; }

    public string FolderDetail => FolderPath is null
        ? "Documents, glossaries and notes it can read"
        : FolderPath;

    // ---- row 2, a repository ----

    [ObservableProperty]
    public partial bool RepositorySelected { get; set; }

    [ObservableProperty]
    public partial string? RepositoryPath { get; set; }

    public string RepositoryDetail => RepositoryPath is null
        ? "Resource files and strings from a repository"
        : RepositoryPath;

    // ---- row 3, individual files ----

    [ObservableProperty]
    public partial bool FilesSelected { get; set; }

    public List<string> ChosenFiles { get; } = [];

    /// <summary>"3 files chosen", from the real count.</summary>
    public string FilesDetail => ChosenFiles.Count == 0
        ? "Pick single PDF, DOCX, MD, JSON or TXT files"
        : ChosenFiles.Count == 1 ? "1 file chosen" : $"{ChosenFiles.Count} files chosen";

    // ---- rows 4 and 5, the chat-derived sources ----

    [ObservableProperty]
    public partial bool AllChatsSelected { get; set; }

    [ObservableProperty]
    public partial bool ChosenTermsSelected { get; set; }

    public bool HasChats => ChatCount > 0;

    /// <summary>A live count, for example "2 chats - 412 translated words".</summary>
    public string AllChatsDetail => HasChats
        ? $"{ChatCount} {(ChatCount == 1 ? "chat" : "chats")} - {TranslatedWords} translated {(TranslatedWords == 1 ? "word" : "words")}"
        : ChatGateReason;

    public string ChosenTermsDetail => HasChats
        ? "Hand-pick terms from your chats instead of taking all of them"
        : ChatGateReason;

    /// <summary>Stated on the row and in its automation name, not implied.</summary>
    public static string ChatGateReason => "No chats yet - this needs at least one translation";

    public bool CanStart =>
        FolderSelected && FolderPath is not null
        || RepositorySelected && RepositoryPath is not null
        || FilesSelected && ChosenFiles.Count > 0
        || AllChatsSelected && HasChats
        || ChosenTermsSelected && HasChats;

    /// <summary>Stated where the note sits, rather than left to a grey button.</summary>
    public string? BlockedReason => CanStart ? null : "Pick at least one";

    /// <summary>
    /// Names the real selection, for example "Index folder and 3 files".
    /// </summary>
    public string ActionLabel => BuildActionLabel(
        FolderSelected && FolderPath is not null,
        RepositorySelected && RepositoryPath is not null,
        FilesSelected ? ChosenFiles.Count : 0,
        AllChatsSelected && HasChats,
        ChosenTermsSelected && HasChats);

    /// <summary>
    /// Builds the action label from what is actually selected, for example
    /// "Index folder and 3 files".
    /// </summary>
    public static string BuildActionLabel(bool folder, bool repository, int files, bool chats, bool terms)
    {
        var parts = new List<string>();

        if (folder)
        {
            parts.Add("folder");
        }

        if (repository)
        {
            parts.Add("repository");
        }

        if (files > 0)
        {
            parts.Add(files == 1 ? "1 file" : $"{files} files");
        }

        if (chats)
        {
            parts.Add("chats");
        }

        if (terms)
        {
            parts.Add("chosen terms");
        }

        if (parts.Count == 0)
        {
            return "Start indexing";
        }

        var listed = parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

        return "Index " + listed;
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to index", Multiselect = false };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        FolderPath = dialog.FolderName;
        FolderSelected = true;
    }

    [RelayCommand]
    private void ChooseRepository()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a repository to index", Multiselect = false };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RepositoryPath = dialog.FolderName;
        RepositorySelected = true;
    }

    [RelayCommand]
    private void PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pick files to index",
            Multiselect = true,
            Filter = "Documents (*.pdf;*.docx;*.md;*.json;*.txt)|*.pdf;*.docx;*.md;*.json;*.txt",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ChosenFiles.Clear();
        ChosenFiles.AddRange(dialog.FileNames);
        FilesSelected = ChosenFiles.Count > 0;

        OnPropertyChanged(nameof(FilesDetail));
        RaiseAction();
    }

    [RelayCommand]
    private void Cancel() => _close();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        var folders = new List<string>();
        if (FolderSelected && FolderPath is not null)
        {
            folders.Add(FolderPath);
        }

        var repositories = new List<string>();
        if (RepositorySelected && RepositoryPath is not null)
        {
            repositories.Add(RepositoryPath);
        }

        var request = new IndexingRequest
        {
            Folders = folders,
            Repositories = repositories,
            Files = FilesSelected ? [.. ChosenFiles] : [],
        };

        _close();
        await _startIndexing(request).ConfigureAwait(true);
    }

    partial void OnFolderSelectedChanged(bool value) => RaiseAction();

    partial void OnFolderPathChanged(string? value)
    {
        OnPropertyChanged(nameof(FolderDetail));
        RaiseAction();
    }

    partial void OnRepositorySelectedChanged(bool value) => RaiseAction();

    partial void OnRepositoryPathChanged(string? value)
    {
        OnPropertyChanged(nameof(RepositoryDetail));
        RaiseAction();
    }

    partial void OnFilesSelectedChanged(bool value) => RaiseAction();

    partial void OnAllChatsSelectedChanged(bool value) => RaiseAction();

    partial void OnChosenTermsSelectedChanged(bool value) => RaiseAction();

    private void RaiseAction()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(BlockedReason));
    }
}
