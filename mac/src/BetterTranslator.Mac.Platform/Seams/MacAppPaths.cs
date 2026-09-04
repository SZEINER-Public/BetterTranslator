using BetterTranslator.Core.Services;
using BetterTranslator.Mac.Seams;

namespace BetterTranslator.Mac.Platform;

public sealed class MacAppPaths : IAppPathsSeam
{
    public const string SupportFolderName = "BetterTranslator";

    public MacAppPaths()
        : this(DefaultRoot())
    {
    }

    public MacAppPaths(string root)
    {
        Root = root;
        Core = new AppPaths(root);
    }

    public static string ApplicationSupport => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support");

    public static string DefaultRoot() => Path.Combine(ApplicationSupport, SupportFolderName);

    public AppPaths Core { get; }

    public string Root { get; }

    public string Models => Core.ModelsFolder;

    public string Runtime => Path.Combine(Root, "runtime");

    public string Cache => Path.Combine(Root, "cache");

    public string Logs => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Core.EnsureCreated();
        Directory.CreateDirectory(Runtime);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
    }
}
