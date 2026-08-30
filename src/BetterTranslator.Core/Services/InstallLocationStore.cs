using System.Text;
using System.Text.Json;

namespace BetterTranslator.Core.Services;

public sealed record StoredInstallLocation(string? Folder, string? Warning)
{
    public static StoredInstallLocation Unset { get; } = new(null, null);
}

public sealed class InstallLocationStore
{
    private const string StoreFileName = "install-location.json";

    private const string FolderProperty = "modelsFolder";

    private readonly string? _legacyFilePath;

    public InstallLocationStore(string filePath, string? legacyFilePath = null)
    {
        FilePath = filePath;
        _legacyFilePath = legacyFilePath;
    }

    public static string FileName => StoreFileName;

    public static string BesideRunningAssembly => Path.Combine(AppContext.BaseDirectory, StoreFileName);

    public string FilePath { get; }

    public StoredInstallLocation Load()
    {
        var stored = Read(FilePath) ?? CarryForward();

        if (stored is null)
        {
            return StoredInstallLocation.Unset;
        }

        return stored.Folder is null ? stored : stored with { Warning = Complaint(stored.Folder) };
    }

    public string? Save(string? folder)
    {
        var staged = FilePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(staged, Serialize(folder));
            File.Move(staged, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Discard(staged);

            return "The chosen folder could not be saved for the next start: " + ex.Message;
        }

        return folder is null ? null : Complaint(folder);
    }

    private StoredInstallLocation? CarryForward()
    {
        if (_legacyFilePath is null)
        {
            return null;
        }

        var legacy = Read(_legacyFilePath);

        if (legacy?.Folder is null)
        {
            return null;
        }

        Save(legacy.Folder);

        return legacy;
    }

    private static byte[] Serialize(string? folder)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            if (folder is null)
            {
                writer.WriteNull(FolderProperty);
            }
            else
            {
                writer.WriteString(FolderProperty, folder);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static StoredInstallLocation? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new StoredInstallLocation(null, Unreadable(path, "it is not a settings document"));
            }

            var folder = document.RootElement.TryGetProperty(FolderProperty, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim()
                    : null;

            return new StoredInstallLocation(folder is { Length: > 0 } ? folder : null, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new StoredInstallLocation(null, Unreadable(path, ex.Message));
        }
    }

    private static string Unreadable(string path, string reason) =>
        "The remembered folder for installed files could not be read from " + path +
        ", so the default folder is in use: " + reason;

    private static string? Complaint(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return "The folder chosen for installed files is not reachable: " + folder +
                    ". It is still the folder in use, so installs will fail until it is available again.";
            }

            var probe = Path.Combine(folder, ".bt-install-probe-" + Guid.NewGuid().ToString("N"));

            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return "The folder chosen for installed files cannot be written to: " + folder +
                ". It is still the folder in use.";
        }
    }

    private static void Discard(string staged)
    {
        try
        {
            if (File.Exists(staged))
            {
                File.Delete(staged);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
