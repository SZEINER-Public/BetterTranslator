namespace BetterTranslator.Updates.Install;

public sealed class UpdatePaths
{
    public const string ServiceName = "BetterTranslatorUpdater";

    public const string ServiceDisplayName = "BetterTranslator automatic updates";

    public const string ServiceDescription =
        "Checks for a newer BetterTranslator release, verifies it against its published checksum and stages it for install. "
        + "Writes only to files under ProgramData and raises no window.";

    public const string ServiceExecutableName = "BetterTranslatorUpdater.exe";

    public const string PayloadAssetName = "BetterTranslator.exe";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BetterTranslator");

    public UpdatePaths()
        : this(DefaultRoot)
    {
    }

    public UpdatePaths(string root) => Root = root;

    public string Root { get; }

    public string UpdatesFolder => Path.Combine(Root, "updates");

    public string StagingFolder => Path.Combine(UpdatesFolder, "staging");

    public string CacheFolder => Path.Combine(UpdatesFolder, "cache");

    public string ReadyFile => Path.Combine(UpdatesFolder, "ready.json");

    public string UpdaterFolder => Path.Combine(Root, "updater");

    public string LogFolder => Path.Combine(UpdaterFolder, "logs");

    public string ServiceFolder => Path.Combine(UpdaterFolder, "service");

    public string ServiceExecutable => Path.Combine(ServiceFolder, ServiceExecutableName);

    public string StagedPayload => Path.Combine(StagingFolder, PayloadAssetName);

    public string PartialPayload => Path.Combine(StagingFolder, PayloadAssetName + ".part");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(StagingFolder);
        Directory.CreateDirectory(CacheFolder);
        Directory.CreateDirectory(LogFolder);
    }
}
