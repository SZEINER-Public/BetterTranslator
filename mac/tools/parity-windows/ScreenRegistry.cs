using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.App.Views;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;
using BetterTranslator.Runtime.Downloads;

namespace BetterTranslator.Mac.Parity.Windows;

public static class ScreenRegistry
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid ChatOne = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ChatTwo = new("22222222-2222-2222-2222-222222222222");

    private static AppPaths _paths = null!;
    private static Database _database = null!;
    private static InstallPaths _installPaths = null!;
    private static ClockService _clock = null!;
    private static HttpClient _http = null!;

    public static IReadOnlyList<Screen> All()
    {
        Seed();

        return
        [
            new("sidebar-no-chats", "empty", () => Host(new SidebarView { DataContext = EmptyWorkspace() }, 246)),
            new("sidebar-has-chats", "populated", () => Host(new SidebarView { DataContext = Workspace() }, 246)),
            new("sidebar-search-open", "search open", () => Host(SearchOpen(), 246)),

            new("chat-empty", "empty", () => new ChatView { DataContext = EmptyWorkspace() }),
            new("chat-populated", "populated, two column rows", () => new ChatView { DataContext = Workspace() }),
            new("chat-skeleton", "loading skeleton", () => new ChatView { DataContext = Skeleton() }),

            new("settings-memory", "memory tab", () => Settings(SettingsTab.Memory)),
            new("settings-your-data", "your data tab", () => Settings(SettingsTab.YourData)),
            new("settings-runtime", "runtime tab", () => Settings(SettingsTab.Runtime)),
            new("settings-downloads", "downloads tab", () => Settings(SettingsTab.Downloads)),
            new("settings-agent", "agent tab", () => Settings(SettingsTab.Agent)),
            new("settings-config", "config tab", () => Settings(SettingsTab.Config)),
            new("settings-updates", "updates tab", () => Settings(SettingsTab.Updates)),

            new("first-run-choosing", "stage one", () => new FirstRunView { DataContext = FirstRun() }),

            new("add-sources-empty", "nothing selected", () => new AddSourcesView { DataContext = AddSources(false, null, 0, 0) }),
            new("add-sources-files-chosen", "files chosen", () => new AddSourcesView { DataContext = AddSources(true, "release-notes", 2, 1840) }),

            new("file-preview-source", "source version", () => Host(new FilePreviewView { DataContext = Workspace().Preview }, 390)),

            new("retrieval-map-empty", "empty", () => new RetrievalMapView { DataContext = new RetrievalMapViewModel() }),

            new("resize-dividers", "both separators with the centre grab indicator", DividerBoard.Build),
        ];
    }

    private static void Seed()
    {
        var root = Path.Combine(MacFolder(), "parity-out", ".seed-windows");

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);

        _paths = new AppPaths(root);
        _database = new Database(_paths);
        _installPaths = new InstallPaths(_paths, new InstallLocationStore(_paths.InstallLocationFile, root));
        _clock = new ClockService();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

        _database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        var store = new ChatStore(_database);

        store.AddChatAsync(new Chat
        {
            Id = ChatOne,
            Name = "Release notes",
            CreatedAt = Epoch,
            UpdatedAt = Epoch,
            IsPinned = true,
            NameIsProvisional = false,
        }, CancellationToken.None).GetAwaiter().GetResult();

        store.AddChatAsync(new Chat
        {
            Id = ChatTwo,
            Name = "Support reply",
            CreatedAt = Epoch.AddMinutes(-30),
            UpdatedAt = Epoch.AddMinutes(-30),
            IsPinned = false,
            NameIsProvisional = false,
        }, CancellationToken.None).GetAwaiter().GetResult();

        store.AddEntryAsync(new Entry
        {
            Id = new Guid("aaaaaaaa-0000-0000-0000-000000000001"),
            ChatId = ChatOne,
            Kind = EntryKind.Sentence,
            Source = "The runtime loads the model once and keeps it resident.",
            Result = "Runtime nacte model jednou a ponecha jej v pameti.",
            TargetLanguage = "Czech",
            SourceCode = "en",
            TargetCode = "cs",
            CreatedAt = Epoch,
            State = EntryState.Done,
            GeneratedTokens = 24,
            DurationMs = 1840,
        }, CancellationToken.None).GetAwaiter().GetResult();

        store.AddEntryAsync(new Entry
        {
            Id = new Guid("aaaaaaaa-0000-0000-0000-000000000002"),
            ChatId = ChatOne,
            Kind = EntryKind.Words,
            Source = "resident, offscreen, headless",
            Result = "rezidentni, mimo obrazovku, bez hlavy",
            TargetLanguage = "Czech",
            SourceCode = "en",
            TargetCode = "cs",
            CreatedAt = Epoch.AddMinutes(1),
            State = EntryState.Done,
        }, CancellationToken.None).GetAwaiter().GetResult();

        store.AddEntryAsync(new Entry
        {
            Id = new Guid("aaaaaaaa-0000-0000-0000-000000000003"),
            ChatId = ChatOne,
            Kind = EntryKind.File,
            Source = "release-notes.md",
            Result = string.Empty,
            TargetLanguage = "Czech",
            SourceCode = "en",
            TargetCode = "cs",
            CreatedAt = Epoch.AddMinutes(2),
            State = EntryState.Pending,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ChatWorkspaceViewModel Workspace()
    {
        var workspace = new ChatWorkspaceViewModel(new ChatStore(_database), _clock, dialog => dialog);

        workspace.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        return workspace;
    }

    private static ChatWorkspaceViewModel EmptyWorkspace()
    {
        var root = Path.Combine(MacFolder(), "parity-out", ".seed-windows-empty");

        Directory.CreateDirectory(root);

        var database = new Database(new AppPaths(root));
        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        var workspace = new ChatWorkspaceViewModel(new ChatStore(database), _clock, dialog => dialog);
        workspace.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        return workspace;
    }

    private static FrameworkElement SearchOpen()
    {
        var workspace = Workspace();
        workspace.IsSearchOpen = true;

        return new SidebarView { DataContext = workspace };
    }

    private static ChatWorkspaceViewModel Skeleton()
    {
        var workspace = Workspace();

        foreach (var entry in workspace.Entries)
        {
            entry.ShowsSkeleton = true;
        }

        return workspace;
    }

    private static FrameworkElement Settings(SettingsTab tab)
    {
        var settings = new SettingsViewModel(
            new SettingsStore(_database),
            new CacheInspector(_paths, _database),
            _installPaths,
            _ => Task.CompletedTask,
            () => { },
            () => Task.CompletedTask,
            () => { });

        settings.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        settings.Tab = tab;

        return new SettingsView { DataContext = settings };
    }

    private static FirstRunViewModel FirstRun() => new(_installPaths, _http);

    private static AddSourcesViewModel AddSources(bool anythingIndexed, string? projectName, int chatCount, int translatedWords) =>
        new(anythingIndexed, projectName, chatCount, translatedWords, _ => Task.CompletedTask, () => { });

    private static FrameworkElement Host(FrameworkElement content, double width) => new Border
    {
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = content,
    };

    private static string MacFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.Name != "mac")
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The mac folder could not be located from " + AppContext.BaseDirectory + ".");
    }
}
