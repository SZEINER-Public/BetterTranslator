using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Downloads;

public static class ComponentInstallState
{
    public static long BytesOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : -1;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public static bool ArtifactMatches(string path, long expectedBytes)
    {
        var length = BytesOf(path);

        return expectedBytes > 0 ? length == expectedBytes : length > 0;
    }

    public static bool IsInstalledIn(string folder, ModelComponent component) =>
        ArtifactMatches(Path.Combine(folder, component.FileName), component.SizeBytes)
        && MissingIn(folder, component).Count == 0;

    public static IReadOnlyList<CompanionArtifact> MissingIn(string folder, ModelComponent component) =>
        [.. component.Companions.Where(c => !ArtifactMatches(Path.Combine(folder, c.FileName), c.SizeBytes))];
}
