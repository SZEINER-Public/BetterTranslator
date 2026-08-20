using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterTranslator.Core.Services;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Service;

namespace BetterTranslator.Updates.Install;

[SupportedOSPlatform("windows")]
public sealed class UpdaterServiceInstaller(UpdatePaths paths, IUpdateLog? log = null)
{
    private readonly IUpdateLog _log = log ?? NullUpdateLog.Instance;

    public static string SourceFolder => Path.Combine(AppContext.BaseDirectory, "updater");

    public ServiceOutcome Install(string sourceFolder, string installedExecutable)
    {
        var source = Path.Combine(sourceFolder, UpdatePaths.ServiceExecutableName);

        if (!File.Exists(source))
        {
            return ServiceOutcome.Failed(
                $"The updater was not found beside the application at {sourceFolder}.");
        }

        if (!InstalledAppStore.IsPlausible(installedExecutable) || !File.Exists(installedExecutable))
        {
            return ServiceOutcome.Failed("The installed application could not be located, so nothing was registered.");
        }

        try
        {
            paths.EnsureCreated();
            Directory.CreateDirectory(paths.ServiceFolder);
            Protect(paths.UpdatesFolder);
            Protect(paths.ServiceFolder);
            Copy(sourceFolder, paths.ServiceFolder);
            new InstalledAppStore(paths).Write(installedExecutable, BuildIdentity.Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("The updater could not be copied into place", ex);

            return ServiceOutcome.Failed($"The updater could not be copied into place: {ex.Message}");
        }

        var registered = ServiceControl.Install(
            UpdatePaths.ServiceName,
            UpdatePaths.ServiceDisplayName,
            UpdatePaths.ServiceDescription,
            paths.ServiceExecutable);

        if (!registered.Ok)
        {
            return registered;
        }

        var started = ServiceControl.Start(UpdatePaths.ServiceName);

        return started.Ok
            ? ServiceOutcome.Done("Automatic updates are on.")
            : started;
    }

    public ServiceOutcome Remove()
    {
        var removed = ServiceControl.Remove(UpdatePaths.ServiceName);

        new StagedUpdateCleanup(paths).Run();

        return removed.Ok ? ServiceOutcome.Done("Automatic updates are off.") : removed;
    }

    private static void Copy(string from, string to)
    {
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            var folder = Path.GetDirectoryName(target);

            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            File.Copy(file, target, overwrite: true);
        }
    }

    private static void Protect(string folder)
    {
        var directory = new DirectoryInfo(folder);
        var security = new DirectorySecurity();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directory.SetAccessControl(security);
    }
}

public sealed class StagedUpdateCleanup(UpdatePaths paths)
{
    public void Run()
    {
        new UpdateApplier(paths).Discard();
        new InstalledAppStore(paths).Clear();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(paths.ServiceFolder))
                {
                    Directory.Delete(paths.ServiceFolder, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
