using System.IO;
using System.Net;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Agents.Mcp;
using BetterTranslator.Runtime.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterTranslator.App.Mcp;

public sealed record McpStartup(bool Started, string Detail);

public sealed class McpServerHost : IAsyncDisposable
{
    private readonly Func<Task<TranslationGateway>> _gateway;
    private readonly IGuiBridge _gui;

    private WebApplication? _app;
    private TranslationGateway? _open;

    public McpServerHost(IGuiBridge gui)
        : this(gui, DefaultGateway)
    {
    }

    /// <summary>
    /// How the application's own agent server opens a gateway: as a guest. The
    /// window owns the runtime, and the backend it loaded stays loaded whatever
    /// the stored setting has since become.
    ///
    /// Named rather than written inline in the constructor so a test can assert
    /// the guest half, which is the entire fix and is otherwise reachable only
    /// by opening a real gateway over the real data folder.
    /// </summary>
    internal static Func<Task<TranslationGateway>> DefaultGateway { get; } =
        () => TranslationGateway.StartAsync(CancellationToken.None, AppOwnsRuntime);

    /// <summary>
    /// False, and the reason is the whole of it: the window resolved the native
    /// backend at startup and asks for a restart to change it, so a server
    /// starting inside that process must not apply a newer stored setting over
    /// the flavour already loaded.
    /// </summary>
    internal const bool AppOwnsRuntime = false;

    /// <summary>The factory this host was built with, so a test can see which one.</summary>
    internal Func<Task<TranslationGateway>> GatewayFactory => _gateway;

    public McpServerHost(IGuiBridge gui, Func<Task<TranslationGateway>> gateway)
    {
        _gui = gui;
        _gateway = gateway;
    }

    public bool IsListening => _app is not null;

    public string? Address { get; private set; }

    public static string RegistrationCommand(string host, int port, string token)
    {
        var command = $"claude mcp add --transport http bettertranslator http://{host}:{port}/mcp";

        return token.Length == 0 ? command : command + $" --header \"Authorization: Bearer {token}\"";
    }

    public static string StdioExecutable()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "cli", "bt.exe"),
            Path.Combine(AppContext.BaseDirectory, "bt.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    public static string StdioCommand()
    {
        var executable = StdioExecutable();

        return executable.Length == 0
            ? "bt.exe is not beside the application. Publish it, or point the agent at the file yourself."
            : $"claude mcp add -s user bettertranslator -- \"{executable}\" mcp";
    }

    public static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    public static string? Refuse(AppSettings settings)
    {
        if (settings.McpPort is < 1 or > 65535)
        {
            return $"{settings.McpPort} is not a port. Choose a number between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(settings.McpHost))
        {
            return "The host is empty. Use 127.0.0.1 to listen on this machine only.";
        }

        if (!IsLoopback(settings.McpHost) && settings.McpToken.Length == 0)
        {
            return $"Binding {settings.McpHost} opens this machine to the network, so a bearer token is required. "
                + "Set a token, or bind 127.0.0.1 instead.";
        }

        return null;
    }

    public async Task<McpStartup> StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return new McpStartup(true, $"Already listening on {Address}.");
        }

        var refusal = Refuse(settings);

        if (refusal is not null)
        {
            return new McpStartup(false, refusal);
        }

        try
        {
            _open = await _gateway().ConfigureAwait(false);

            // So a translation an agent asks for appears in the window it is
            // running inside, rather than at the next launch.
            _open.Gui = _gui;

            var builder = WebApplication.CreateSlimBuilder();

            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://{settings.McpHost}:{settings.McpPort}");

            builder.Services.AddSingleton(_open);
            builder.Services.AddSingleton(_gui);
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "bettertranslator",
                        Version = typeof(McpServerHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                    };

                    options.ServerInstructions =
                        "BetterTranslator translates with models running on this machine. "
                        + "Call list_languages for the codes to pass as 'from' and 'to', translate_text for a string, "
                        + "translate_file or translate_batch for paths on disk (both return a job id to poll with job_status).";
                })
                .WithHttpTransport()
                .WithTools(BetterTranslatorTools.Create(_open, _gui));

            var app = builder.Build();

            if (settings.McpToken.Length > 0)
            {
                var expected = "Bearer " + settings.McpToken;

                app.Use(async (context, next) =>
                {
                    if (!string.Equals(context.Request.Headers.Authorization, expected, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });
            }

            app.MapMcp("/mcp");

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            _app = app;
            Address = $"http://{settings.McpHost}:{settings.McpPort}/mcp";

            return new McpStartup(true, $"Listening on {Address}.");
        }
        catch (Exception ex)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);

            return new McpStartup(false, ex is IOException
                ? $"Port {settings.McpPort} is already in use. Choose another port."
                : ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        _open?.Dispose();
        _open = null;
        Address = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
