using System.Net.Http;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;
using BetterTranslator.Mac.Adapters;
using BetterTranslator.Mac.Platform;
using BetterTranslator.Mac.Seams;
using BetterTranslator.Runtime.Downloads;

namespace BetterTranslator.Mac.Parity;

public sealed class SeedFactory
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid ChatOne = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ChatTwo = new("22222222-2222-2222-2222-222222222222");

    private SeedFactory(string root)
    {
        Root = root;
        Paths = new AppPaths(root);
        Database = new Database(Paths);
        InstallPaths = new InstallPaths(Paths, new InstallLocationStore(Paths.InstallLocationFile, root));
        Clock = new ClockService();
        Backends = new MacInferenceBackends();
        Updates = new MacUpdateTrigger();
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public Database Database { get; }

    public InstallPaths InstallPaths { get; }

    public ClockService Clock { get; }

    public MacInferenceBackends Backends { get; }

    public MacUpdateTrigger Updates { get; }

    public HttpClient Http { get; }

    public static async Task<SeedFactory> CreateAsync(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);

        var factory = new SeedFactory(root);

        await factory.Database.MigrateAsync(CancellationToken.None).ConfigureAwait(false);
        await factory.SeedChatsAsync().ConfigureAwait(false);

        return factory;
    }

    private async Task SeedChatsAsync()
    {
        var store = new ChatStore(Database);

        await store.AddChatAsync(new Chat
        {
            Id = ChatOne,
            Name = "Release notes",
            CreatedAt = Epoch,
            UpdatedAt = Epoch,
            IsPinned = true,
            NameIsProvisional = false,
        }, CancellationToken.None).ConfigureAwait(false);

        await store.AddChatAsync(new Chat
        {
            Id = ChatTwo,
            Name = "Support reply",
            CreatedAt = Epoch.AddMinutes(-30),
            UpdatedAt = Epoch.AddMinutes(-30),
            IsPinned = false,
            NameIsProvisional = false,
        }, CancellationToken.None).ConfigureAwait(false);

        await store.AddEntryAsync(new Entry
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
        }, CancellationToken.None).ConfigureAwait(false);

        await store.AddEntryAsync(new Entry
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
        }, CancellationToken.None).ConfigureAwait(false);

        await store.AddEntryAsync(new Entry
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
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public ChatWorkspaceViewModel EmptyWorkspace()
    {
        var root = Path.Combine(Root, "empty");

        Directory.CreateDirectory(root);

        var paths = new AppPaths(root);
        var database = new Database(paths);

        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        var workspace = new ChatWorkspaceViewModel(new ChatStore(database), Clock, dialog => dialog);

        workspace.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        return workspace;
    }

    public ChatWorkspaceViewModel Workspace()
    {
        var workspace = new ChatWorkspaceViewModel(new ChatStore(Database), Clock, dialog => dialog);

        workspace.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        return workspace;
    }

    public SettingsViewModel Settings()
    {
        var settings = new SettingsViewModel(
            new SettingsStore(Database),
            new CacheInspector(Paths, Database),
            InstallPaths,
            _ => Task.CompletedTask,
            () => { },
            () => Task.CompletedTask,
            () => { });

        settings.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        return settings;
    }

    public MacSettingsSurface SettingsSurface() => new(Settings(), Backends, Updates);

    public FirstRunViewModel FirstRun() => new(InstallPaths, Http);

    public MacInstallSurface InstallSurface() => new(FirstRun());

    public MemoryViewModel Memory()
    {
        var store = new IndexStore(Database);

        return new MemoryViewModel(store, new IndexingService(store, new DocumentReaders()));
    }

    public RetrievalMapViewModel Map() => new();

    public AddSourcesViewModel AddSources(bool anythingIndexed, string? projectName, int chatCount, int translatedWords) =>
        new(anythingIndexed, projectName, chatCount, translatedWords, _ => Task.CompletedTask, () => { });

    public TranslationSettingsViewModel Translation() => new(InstallPaths, () => { });
}
