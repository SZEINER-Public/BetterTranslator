using Avalonia.Controls;
using Avalonia.Layout;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Mac.App.Views;

namespace BetterTranslator.Mac.Parity;

public static class SeededContent
{
    private static SeedFactory? _seed;

    public static void Prepare(string parityOut)
    {
        var root = Path.Combine(parityOut, ".seed");

        _seed = SeedFactory.CreateAsync(root).GetAwaiter().GetResult();
    }

    private static SeedFactory Seed =>
        _seed ?? throw new InvalidOperationException("SeededContent.Prepare was not called.");

    public static IReadOnlyList<ScreenDefinition> Register() =>
    [
        new("sidebar-no-chats", "empty", () => Rail(new SidebarView { DataContext = Seed.EmptyWorkspace() })),
        new("sidebar-has-chats", "populated", () => Rail(new SidebarView { DataContext = Seed.Workspace() })),
        new("sidebar-search-open", "search field open", () => Rail(new SidebarView { DataContext = SearchOpen() })),

        new("chat-empty", "empty", () => new ChatView { DataContext = Seed.EmptyWorkspace() }),
        new("chat-populated", "two column rows and a file row", () => new ChatView { DataContext = Seed.Workspace() }),
        new("chat-skeleton", "loading skeleton", () => new ChatView { DataContext = Skeleton() }),

        new("settings-memory", "memory tab", () => Settings(SettingsTab.Memory)),
        new("settings-your-data", "your data tab", () => Settings(SettingsTab.YourData)),
        new("settings-runtime", "runtime tab, CPU only statement", () => Settings(SettingsTab.Runtime)),
        new("settings-downloads", "downloads tab", () => Settings(SettingsTab.Downloads)),
        new("settings-agent", "agent tab", () => Settings(SettingsTab.Agent)),
        new("settings-config", "config tab", () => Settings(SettingsTab.Config)),
        new("settings-updates", "updates tab", () => Settings(SettingsTab.Updates)),

        new("first-run-choosing", "stage one, choosing components", () => new FirstRunView { DataContext = Seed.InstallSurface() }),

        new("add-sources-empty", "nothing selected", () => new AddSourcesView { DataContext = Seed.AddSources(false, null, 0, 0) }),
        new("add-sources-files-chosen", "sources chosen", () => new AddSourcesView { DataContext = Seed.AddSources(true, "release-notes", 2, 1840) }),

        new("file-preview-source", "source version", () => Panel(new FilePreviewView { DataContext = Seed.Workspace().Preview }, 390)),

        new("retrieval-map-empty", "empty", () => new RetrievalMapView { DataContext = Seed.Map() }),

        new("resize-dividers", "both separators with the centre grab indicator", DividerBoard.Build),
    ];

    private static Control Rail(Control content) => Panel(content, 246);

    private static Control Panel(Control content, double width) => new Border
    {
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = content,
    };

    private static Control Settings(SettingsTab tab)
    {
        var surface = Seed.SettingsSurface();

        surface.Settings.Tab = tab;

        return new SettingsView { DataContext = surface };
    }

    private static ChatWorkspaceViewModel SearchOpen()
    {
        var workspace = Seed.Workspace();

        workspace.IsSearchOpen = true;

        return workspace;
    }

    private static ChatWorkspaceViewModel Skeleton()
    {
        var workspace = Seed.Workspace();

        foreach (var entry in workspace.Entries)
        {
            entry.ShowsSkeleton = true;
        }

        return workspace;
    }
}
