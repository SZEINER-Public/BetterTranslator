using System.Runtime.InteropServices;
using BetterTranslator.Updates.Logging;
using BetterTranslator.Updates.Payload;

namespace BetterTranslator.Updates.Install;

public enum ApplyState
{
    Applied,
    HeldForRestart,
    HeldForReboot,
    Refused,
}

public sealed record ApplyVerdict(ApplyState State, string Detail)
{
    public bool InstalledBuildTouched => State is ApplyState.Applied or ApplyState.HeldForReboot;
}

public sealed class UpdateApplier(UpdatePaths paths, IUpdateLog? log = null)
{
    private const uint ReplaceExisting = 0x1;
    private const uint DelayUntilReboot = 0x4;
    private const string SupersededSuffix = ".superseded-";

    private readonly IUpdateLog _log = log ?? NullUpdateLog.Instance;

    public async Task<ApplyVerdict> ApplyAsync(
        string installedExecutable,
        StagedUpdate staged,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!InstalledAppStore.IsPlausible(installedExecutable) || !File.Exists(installedExecutable))
        {
            return new ApplyVerdict(ApplyState.Refused, "The installed build was not found where it was recorded.");
        }

        if (!File.Exists(staged.File))
        {
            new StagedUpdateStore(paths.ReadyFile).Clear();

            return new ApplyVerdict(ApplyState.Refused, "The staged build has gone from the staging folder.");
        }

        var verdict = await PayloadVerifier
            .VerifyAsync(staged.File, expectedSha256, staged.SizeBytes, cancellationToken)
            .ConfigureAwait(false);

        if (!verdict.Accepted)
        {
            Discard();
            _log.Write($"Refused to apply the staged build: {verdict.Detail}");

            return new ApplyVerdict(ApplyState.Refused, verdict.Detail);
        }

        if (AppProcess.IsRunning(installedExecutable))
        {
            return new ApplyVerdict(
                ApplyState.HeldForRestart,
                $"{staged.Version} is staged and verified. It goes in when the application is next closed.");
        }

        var aside = Aside(installedExecutable, staged.Version);

        try
        {
            RemoveQuietly(aside);
            File.Move(installedExecutable, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("The installed build could not be moved aside", ex);

            return Delayed(staged, installedExecutable, ex);
        }

        try
        {
            File.Copy(staged.File, installedExecutable, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("The staged build could not be put into place, rolling back", ex);

            try
            {
                File.Move(aside, installedExecutable);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                _log.Write("The rollback failed as well", rollback);

                return new ApplyVerdict(
                    ApplyState.Refused,
                    $"The install could not be completed and the previous build is at {aside}.");
            }

            return new ApplyVerdict(ApplyState.Refused, $"The install could not be completed: {ex.Message}");
        }

        RemoveQuietly(staged.File);
        new StagedUpdateStore(paths.ReadyFile).Clear();
        _log.Write($"Installed {staged.Version} at {installedExecutable}. The previous build waits at {aside}.");

        return new ApplyVerdict(ApplyState.Applied, $"{staged.Version} is installed.");
    }

    public void RemoveSupersededBuilds(string installedExecutable)
    {
        var folder = Path.GetDirectoryName(installedExecutable);

        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        var prefix = Path.GetFileName(installedExecutable) + SupersededSuffix;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(installedExecutable) + SupersededSuffix + "*"))
            {
                if (Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveQuietly(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Discard()
    {
        RemoveQuietly(paths.StagedPayload);
        RemoveQuietly(paths.PartialPayload);
        new StagedUpdateStore(paths.ReadyFile).Clear();
    }

    private ApplyVerdict Delayed(StagedUpdate staged, string installedExecutable, Exception cause)
    {
        if (!OperatingSystem.IsWindows()
            || !MoveFileEx(staged.File, installedExecutable, ReplaceExisting | DelayUntilReboot))
        {
            return new ApplyVerdict(
                ApplyState.Refused,
                $"The installed build could not be replaced and was left as it is: {cause.Message}");
        }

        _log.Write($"{staged.Version} goes in on the next restart of this machine.");

        return new ApplyVerdict(
            ApplyState.HeldForReboot,
            $"{staged.Version} is verified and goes in when this machine next restarts.");
    }

    private static string Aside(string installedExecutable, string version)
    {
        var safe = new string(version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').Take(32).ToArray());

        return installedExecutable + SupersededSuffix + (safe.Length == 0 ? "previous" : safe);
    }

    private static void RemoveQuietly(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string replacement, uint flags);
}
