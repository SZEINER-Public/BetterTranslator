namespace BetterTranslator.Updates.Install;

public enum InstallRoute
{
    Direct,
    ServiceHandoff,
    Elevate,
}

public static class InstallLocation
{
    public static InstallRoute Decide(bool writable, bool serviceInstalled) => (writable, serviceInstalled) switch
    {
        (true, _) => InstallRoute.Direct,
        (false, true) => InstallRoute.ServiceHandoff,
        _ => InstallRoute.Elevate,
    };

    public static bool IsWritable(string executable)
    {
        var folder = Path.GetDirectoryName(executable);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var probe = Path.Combine(folder, ".bt-write-probe-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
