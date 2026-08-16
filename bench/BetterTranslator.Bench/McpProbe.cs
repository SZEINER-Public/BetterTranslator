using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterTranslator.Bench;

internal sealed record McpProbeResult(
    bool Reachable,
    string Transport,
    string Endpoint,
    string ServerName,
    string ServerVersion,
    string ProtocolVersion,
    IReadOnlyList<string> Tools,
    bool TranslateFilePresent,
    bool TranslateFileAcceptsInputPath,
    bool TranslateFileAcceptsOutputPath,
    string SelectedModel,
    string SelectedModelFile,
    string SelectedModelQuantization,
    string Detail);

internal static class McpProbe
{
    internal static McpProbeResult Probe(string executable, string transportArgument, TimeSpan timeout)
    {
        if (!File.Exists(executable))
        {
            return Unreachable($"{executable} does not exist.");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        start.ArgumentList.Add(transportArgument);

        using var process = Process.Start(start);

        if (process is null)
        {
            return Unreachable($"{executable} did not start.");
        }

        try
        {
            Send(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "btbench", ["version"] = "1.0" },
                },
            });

            var initialize = Await(process, 1, timeout);

            if (initialize is null)
            {
                return Unreachable("initialize did not answer.");
            }

            Send(process, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

            Send(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/list",
                ["params"] = new JsonObject(),
            });

            var listed = Await(process, 2, timeout);

            if (listed is null)
            {
                return Unreachable("tools/list did not answer.");
            }

            var tools = listed["result"]?["tools"]?.AsArray() ?? [];
            var names = tools.Select(tool => tool?["name"]?.GetValue<string>() ?? string.Empty).Where(name => name.Length > 0).ToList();

            var translateFile = tools.FirstOrDefault(tool => tool?["name"]?.GetValue<string>() == "translate_file");
            var properties = translateFile?["inputSchema"]?["properties"]?.AsObject();
            var acceptsPath = properties?.ContainsKey("path") ?? false;
            var acceptsOutput = properties?.ContainsKey("output") ?? false;

            Send(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "list_models",
                    ["arguments"] = new JsonObject(),
                },
            });

            var models = Await(process, 3, timeout + TimeSpan.FromMinutes(4));
            var selected = SelectedModel(models);
            var raw = models?.ToJsonString(JsonOptions.Compact) ?? "no answer to tools/call list_models";

            return new McpProbeResult(
                Reachable: true,
                Transport: "stdio",
                Endpoint: $"{executable} {transportArgument}",
                ServerName: initialize["result"]?["serverInfo"]?["name"]?.GetValue<string>() ?? string.Empty,
                ServerVersion: initialize["result"]?["serverInfo"]?["version"]?.GetValue<string>() ?? string.Empty,
                ProtocolVersion: initialize["result"]?["protocolVersion"]?.GetValue<string>() ?? string.Empty,
                Tools: names,
                TranslateFilePresent: translateFile is not null,
                TranslateFileAcceptsInputPath: acceptsPath,
                TranslateFileAcceptsOutputPath: acceptsOutput,
                SelectedModel: selected.Name,
                SelectedModelFile: selected.File,
                SelectedModelQuantization: Quantization(selected.File),
                Detail: selected.Name.Length > 0 ? "ok" : (raw.Length <= 600 ? raw : raw[..600]));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static (string Name, string File) SelectedModel(JsonNode? response)
    {
        var text = response?["result"]?["content"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>();

        if (text is null)
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            var payload = JsonNode.Parse(text);
            var models = payload?["models"]?.AsArray() ?? [];
            var chosen = models.FirstOrDefault(model => model?["selected"]?.GetValue<bool>() ?? false);

            return (chosen?["name"]?.GetValue<string>() ?? string.Empty, chosen?["file"]?.GetValue<string>() ?? string.Empty);
        }
        catch (JsonException)
        {
            return SelectedFromTable(text);
        }
    }

    private static (string Name, string File) SelectedFromTable(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var cells = line.Split('|', StringSplitOptions.TrimEntries);

            if (cells.Length < 6 || cells[1] == "name" || cells[1].StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(cells[4], "yes", StringComparison.OrdinalIgnoreCase))
            {
                return (cells[1], cells[2]);
            }
        }

        return (string.Empty, string.Empty);
    }

    private static string Quantization(string file)
    {
        var parts = Path.GetFileNameWithoutExtension(file).Split('-', '.');
        var quant = parts.LastOrDefault(part => part.StartsWith('Q') || part.StartsWith("MXFP") || part.StartsWith("IQ"));

        if (quant is null)
        {
            return string.Empty;
        }

        var index = Path.GetFileNameWithoutExtension(file).IndexOf(quant, StringComparison.Ordinal);

        return index < 0 ? quant : Path.GetFileNameWithoutExtension(file)[index..];
    }

    private static void Send(Process process, JsonObject message)
    {
        process.StandardInput.WriteLine(message.ToJsonString(JsonOptions.Compact));
        process.StandardInput.Flush();
    }

    private static JsonNode? Await(Process process, int id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var line = ReadLine(process, deadline);

            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                continue;
            }

            JsonNode? node;

            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node?["id"]?.GetValue<int>() == id)
            {
                return node;
            }
        }

        return null;
    }

    private static string? ReadLine(Process process, DateTime deadline)
    {
        var read = process.StandardOutput.ReadLineAsync();

        while (!read.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                return null;
            }

            read.Wait(TimeSpan.FromMilliseconds(200));
        }

        return read.Result;
    }

    private static McpProbeResult Unreachable(string detail) => new(
        Reachable: false,
        Transport: "stdio",
        Endpoint: string.Empty,
        ServerName: string.Empty,
        ServerVersion: string.Empty,
        ProtocolVersion: string.Empty,
        Tools: [],
        TranslateFilePresent: false,
        TranslateFileAcceptsInputPath: false,
        TranslateFileAcceptsOutputPath: false,
        SelectedModel: string.Empty,
        SelectedModelFile: string.Empty,
        SelectedModelQuantization: string.Empty,
        Detail: detail);
}
