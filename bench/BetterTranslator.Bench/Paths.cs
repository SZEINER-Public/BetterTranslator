using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterTranslator.Bench;

internal static class Paths
{
    internal static string BenchRoot { get; } = LocateBenchRoot();

    internal static string Corpus => Path.Combine(BenchRoot, "corpus");

    internal static string Prompts => Path.Combine(BenchRoot, "prompts");

    internal static string McpConfig => Path.Combine(BenchRoot, "mcp", "bettertranslator.json");

    internal static string Results => Path.Combine(BenchRoot, "results");

    internal static string Scratch => Path.Combine(BenchRoot, "scratch");

    internal static string RepoRoot => Directory.GetParent(BenchRoot)!.FullName;

    internal static string RunDirectory(string runId) => Path.Combine(Results, runId);

    internal static string LogDirectory(string runId) => Path.Combine(RunDirectory(runId), "logs");

    internal static string ScratchDirectory(string runId) => Path.Combine(Scratch, runId);

    private static string LocateBenchRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (string.Equals(directory.Name, "bench", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

internal static class Hashing
{
    internal static string Sha256OfFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal static string Sha256OfBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

internal static class Files
{
    internal static readonly UTF8Encoding Utf8NoBom = new(false);

    internal static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    internal static void WriteJson<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions.Pretty);
        WriteText(path, json + "\n");
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
