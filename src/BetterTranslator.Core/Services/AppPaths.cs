namespace BetterTranslator.Core.Services;

/// <summary>
/// The application data folder: the database, the caches and the index.
///
/// Normally under LocalApplicationData, but redirectable. The redirect cannot
/// live in the settings table, because that table is inside the database this
/// path locates -- so it is a one-line pointer file at the default location,
/// read before anything is opened.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Where the pointer lives, always at the default root.</summary>
    public static string RedirectFile => Path.Combine(DefaultRoot, "datafolder.txt");

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BetterTranslator");

    public AppPaths()
        : this(ResolveRoot(), DefaultRoot)
    {
    }

    /// <summary>
    /// Follows the pointer if there is one and it names a folder that exists.
    /// A redirect to somewhere that has gone -- an unplugged drive, a deleted
    /// folder -- falls back to the default rather than failing to start, which
    /// leaves the app usable and the old data findable.
    /// </summary>
    private static string ResolveRoot()
    {
        try
        {
            if (File.Exists(RedirectFile))
            {
                var target = File.ReadAllText(RedirectFile).Trim();

                if (target.Length > 0 && Directory.Exists(target))
                {
                    return target;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable pointer is no pointer.
        }

        return DefaultRoot;
    }

    /// <summary>Overridable root, so tests do not touch the real profile.</summary>
    public AppPaths(string root)
        : this(root, root)
    {
    }

    private AppPaths(string root, string profileRoot)
    {
        Root = root;
        ProfileRoot = profileRoot;
    }

    public string Root { get; }

    public string ProfileRoot { get; }

    public string InstallLocationFile => Path.Combine(ProfileRoot, InstallLocationStore.FileName);

    public string DatabaseFile => Path.Combine(Root, "betterTranslator.db");

    public string ModelsFolder => Path.Combine(Root, "models");

    public string ConfigFolder => Path.Combine(Root, "config");

    public string PreviewCacheFolder => Path.Combine(Root, "cache", "previews");

    public string EmbeddingCacheFolder => Path.Combine(Root, "cache", "embeddings");

    /// <summary>
    /// Where a Hunspell pair is looked for. Created empty on first run, because
    /// a folder that exists is the whole instruction for dropping a dictionary
    /// in: nothing to configure and nothing to read first.
    ///
    /// Under the data folder rather than beside the executable so it survives an
    /// update, and so a copy the reader supplies wins over anything shipped.
    /// </summary>
    public string DictionariesFolder => Path.Combine(Root, "dictionaries");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelsFolder);
        Directory.CreateDirectory(DictionariesFolder);
        Directory.CreateDirectory(PreviewCacheFolder);
        Directory.CreateDirectory(EmbeddingCacheFolder);
    }
}
