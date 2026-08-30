namespace BetterTranslator.App.ViewModels;

public sealed record InstallBatch(string Id, IReadOnlyList<string> Installed)
{
    public string Summary => Installed.Count switch
    {
        0 => "Nothing was installed.",
        1 => $"{Installed[0]} is installed.",
        2 => $"{Installed[0]} and {Installed[1]} are installed.",
        _ => $"{string.Join(", ", Installed.Take(Installed.Count - 1))} and {Installed[^1]} are installed.",
    };
}
