using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterTranslator.Core.Services;

namespace BetterTranslator.Updates.Install;

public sealed record InstalledApp
{
    [JsonPropertyName("executable")]
    public string Executable { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("commit")]
    public string Commit { get; init; } = string.Empty;

    [JsonPropertyName("recordedUtc")]
    public DateTimeOffset RecordedUtc { get; init; }
}

public sealed class InstalledAppStore(UpdatePaths paths)
{
    private const int MaxBytes = 4 * 1024;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public string File => Path.Combine(paths.ServiceFolder, "install.json");

    public string? Read() => ReadRecord()?.Executable;

    public InstalledApp? ReadRecord()
    {
        try
        {
            var file = new FileInfo(File);

            if (!file.Exists || file.Length > MaxBytes)
            {
                return null;
            }

            var recorded = JsonSerializer.Deserialize<InstalledApp>(
                System.IO.File.ReadAllText(File, Encoding.UTF8),
                Format);

            var executable = recorded?.Executable ?? string.Empty;

            return IsPlausible(executable) && System.IO.File.Exists(executable) ? recorded : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public BuildIdentity? ReadIdentity()
    {
        var recorded = ReadRecord();

        return recorded is null || recorded.Version.Length == 0
            ? null
            : new BuildIdentity(recorded.Version, recorded.Commit, "stable", recorded.RecordedUtc);
    }

    public void Write(string executable, BuildIdentity identity) =>
        Write(executable, identity.Version, identity.HasCommit ? identity.Commit : string.Empty);

    public void Write(string executable, string version, string commit)
    {
        Directory.CreateDirectory(paths.ServiceFolder);

        System.IO.File.WriteAllText(
            File,
            JsonSerializer.Serialize(
                new InstalledApp
                {
                    Executable = executable,
                    Version = version,
                    Commit = commit,
                    RecordedUtc = DateTimeOffset.UtcNow,
                },
                Format),
            Encoding.UTF8);
    }

    public void Clear()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                System.IO.File.Delete(File);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool IsPlausible(string executable) =>
        executable.Length > 0
        && Path.IsPathFullyQualified(executable)
        && string.Equals(
            Path.GetFileName(executable),
            UpdatePaths.PayloadAssetName,
            StringComparison.OrdinalIgnoreCase);
}

public static class AppProcess
{
    public static bool IsRunning(string executable)
    {
        if (executable.Length == 0)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executable);

        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    var module = process.MainModule?.FileName;

                    if (module is not null
                        && string.Equals(module, executable, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Win32Exception)
                {
                    return true;
                }
                catch (InvalidOperationException)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        return false;
    }
}
