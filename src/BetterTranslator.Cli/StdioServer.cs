using System.Globalization;
using System.IO;
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Agents.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BetterTranslator.Cli;

public static class StdioServer
{
    public static string Banner()
    {
        var name = typeof(StdioServer).Assembly.GetName();
        var version = name.Version?.ToString(3) ?? "1.0.0";
        var launched = Environment.ProcessPath ?? string.Empty;

        var managed = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
        var stamped = File.Exists(managed) ? managed : launched;

        var built = stamped.Length > 0 && File.Exists(stamped)
            ? File.GetLastWriteTime(stamped).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "unknown";

        return $"bettertranslator mcp {version}, built {built}, from {(launched.Length == 0 ? "unknown" : launched)}";
    }

    public static McpServerOptions Options(TranslationGateway gateway, IGuiBridge gui)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);

        foreach (var tool in BetterTranslatorTools.Create(gateway, gui))
        {
            tools.Add(tool);
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "bettertranslator",
                Version = typeof(StdioServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            },
            ServerInstructions =
                "BetterTranslator translates with models running on this machine. "
                + "Call list_languages for the codes to pass as 'from' and 'to', translate_text for a string, "
                + "translate_file or translate_batch for paths on disk (both return a job id to poll with job_status). "
                + "show_in_gui needs the BetterTranslator window and reports that it is unavailable here.",
            ToolCollection = tools,
        };
    }

    public static async Task<int> RunAsync(TranslationGateway gateway, CancellationToken cancellationToken)
    {
        await using var transport = new StdioServerTransport("bettertranslator");
        await using var server = McpServer.Create(transport, Options(gateway, new NoGuiBridge()));

        await server.RunAsync(cancellationToken).ConfigureAwait(false);

        return ExitCode.Success;
    }
}
