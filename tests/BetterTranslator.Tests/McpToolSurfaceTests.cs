using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using BetterTranslator.App.Mcp;
using BetterTranslator.Runtime.Agents.Mcp;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using ModelContextProtocol.Client;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

public sealed class StubEngine(Func<TranslationJob, EngineAnswer> answer) : ITranslationEngine
{
    public List<TranslationJob> Asked { get; } = [];

    public Task<EngineAnswer> TranslateAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        Asked.Add(job);
        return Task.FromResult(answer(job));
    }

    public Task<EngineAnswer> TranslateDocumentAsync(
        TranslationJob job,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        Asked.Add(job);
        return Task.FromResult(answer(job));
    }

    /// <summary>What a chat is named from. Null: the stub names nothing.</summary>
    public Task<string?> CompleteAsync(
        string modelPath,
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<TranslationOutcome> TranslateRawAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        var given = answer(job);

        return Task.FromResult(new TranslationOutcome(given.Text, given.GeneratedTokens, given.Duration));
    }

    public void Dispose()
    {
    }
}

[Collection(EngineConfigCollection.Name)]
public sealed class McpToolSurfaceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-mcp", Guid.NewGuid().ToString("N"));

    private McpServerHost? _host;
    private McpClient? _client;
    private TranslationGateway? _gateway;
    private StubEngine? _engine;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var paths = new AppPaths(_root);
        var database = new Database(paths);
        await database.MigrateAsync(CancellationToken.None);

        _engine = new StubEngine(job => new EngineAnswer(
            "Sestaveni je zelene.",
            null,
            7,
            TimeSpan.FromMilliseconds(120)));

        var settings = new AppSettings { McpEnabled = true, McpHost = "127.0.0.1", McpPort = FreePort() };

        _gateway = new TranslationGateway(
            database,
            new SettingsStore(database),
            new InstallPaths(paths),
            settings,
            _engine);

        _host = new McpServerHost(new NoGuiBridge(), () => Task.FromResult(_gateway));

        var start = await _host.StartAsync(settings, CancellationToken.None);
        start.Started.Should().BeTrue(start.Detail);

        _client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://{settings.McpHost}:{settings.McpPort}/mcp"),
        }));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ToolsListReturnsExactlyTheTenAllowlistedTools()
    {
        var tools = await _client!.ListToolsAsync();

        output.WriteLine(string.Join(", ", tools.Select(t => t.Name)));

        tools.Select(t => t.Name).Should().BeEquivalentTo(BetterTranslatorTools.Names);
        tools.Should().HaveCount(10);
    }

    [Fact]
    public async Task EveryToolCarriesADescriptionSchemasAndAnnotations()
    {
        var tools = await _client!.ListToolsAsync();
        var missing = new List<string>();

        foreach (var tool in tools)
        {
            var protocol = tool.ProtocolTool;

            if (string.IsNullOrWhiteSpace(protocol.Description))
            {
                missing.Add($"{tool.Name}: description");
            }

            if (protocol.InputSchema.ValueKind != JsonValueKind.Object)
            {
                missing.Add($"{tool.Name}: input schema");
            }

            if (protocol.OutputSchema is null || protocol.OutputSchema.Value.ValueKind != JsonValueKind.Object)
            {
                missing.Add($"{tool.Name}: output schema");
            }

            if (protocol.Annotations is null
                || (protocol.Annotations.ReadOnlyHint is null && protocol.Annotations.DestructiveHint is null))
            {
                missing.Add($"{tool.Name}: annotations");
            }
        }

        output.WriteLine(string.Join("\n", missing));

        missing.Should().BeEmpty("a fresh agent picks the tool and its arguments from this metadata alone");
    }

    [Fact]
    public void TheToolsAreBuiltWithOutputSchemasBeforeAnythingIsSerialized()
    {
        var tools = BetterTranslatorTools.Create(_gateway!, new NoGuiBridge());

        foreach (var tool in tools)
        {
            output.WriteLine($"{tool.ProtocolTool.Name}: {tool.ProtocolTool.OutputSchema?.ToString() ?? "NULL"}");
        }

        tools.Where(t => t.ProtocolTool.OutputSchema is null)
            .Select(t => t.ProtocolTool.Name)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task EveryInputParameterIsDescribed()
    {
        var tools = await _client!.ListToolsAsync();
        var undescribed = new List<string>();

        foreach (var tool in tools)
        {
            if (!tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("description", out var description)
                    || string.IsNullOrWhiteSpace(description.GetString()))
                {
                    undescribed.Add($"{tool.Name}.{property.Name}");
                }
            }
        }

        undescribed.Should().BeEmpty("an agent picks arguments from these descriptions alone");
    }

    [Fact]
    public async Task TranslateTextReturnsStructuredContentAndAMarkdownBlock()
    {
        var result = await _client!.CallToolAsync(
            "translate_text",
            new Dictionary<string, object?>
            {
                ["text"] = "The build is green.",
                ["from"] = "en",
                ["to"] = "cs",
            }!);

        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();

        var structured = result.StructuredContent!.Value;

        structured.GetProperty("ok").GetBoolean().Should().BeTrue();
        structured.GetProperty("from").GetString().Should().Be("en");
        structured.GetProperty("to").GetString().Should().Be("cs");
        structured.GetProperty("result").GetString().Should().Be("Sestaveni je zelene.");

        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;

        output.WriteLine(text);

        text.Should().Contain("| field | value |", "the same data has to be readable as a table");
        text.Should().Contain("Sestaveni je zelene.");

        _engine!.Asked.Should().ContainSingle();
        _engine.Asked[0].From.Code.Should().Be("en");
        _engine.Asked[0].To.Code.Should().Be("cs");
    }

    [Fact]
    public async Task AnUnknownLanguageComesBackAsAnActionableToolError()
    {
        var result = await _client!.CallToolAsync(
            "translate_text",
            new Dictionary<string, object?>
            {
                ["text"] = "The build is green.",
                ["from"] = "en",
                ["to"] = "klingon",
            }!);

        result.IsError.Should().BeTrue();

        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;

        output.WriteLine(text);

        text.Should().Contain("klingon");
        text.Should().Contain("list_languages", "the message has to name the way out");
    }

    [Fact]
    public async Task ListLanguagesAnswersFromTheOneRegistry()
    {
        var result = await _client!.CallToolAsync("list_languages", new Dictionary<string, object?>()!);

        result.IsError.Should().NotBe(true);

        var languages = result.StructuredContent!.Value.GetProperty("languages");

        languages.GetArrayLength().Should().Be(51);
        languages.EnumerateArray().Select(l => l.GetProperty("code").GetString())
            .Should().Contain(["en", "cs", "zh-CN"]);
    }

    [Fact]
    public void ANonLoopbackBindWithoutATokenIsRefusedWithASettingsReason()
    {
        var exposed = new AppSettings { McpEnabled = true, McpHost = "0.0.0.0", McpPort = 8765 };

        var refusal = McpServerHost.Refuse(exposed);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("token");
        refusal.Should().Contain("127.0.0.1", "the message has to name the way out");

        exposed.McpToken = "secret";
        McpServerHost.Refuse(exposed).Should().BeNull("a token makes the exposed bind legitimate");

        McpServerHost.Refuse(new AppSettings { McpHost = "127.0.0.1", McpPort = 8765 })
            .Should().BeNull("loopback needs no token");
    }

    [Fact]
    public async Task TheListenerStopsAndResumesWithoutRestartingAnything()
    {
        var settings = new AppSettings { McpEnabled = true, McpHost = "127.0.0.1", McpPort = FreePort() };
        var host = new McpServerHost(new NoGuiBridge(), () => Task.FromResult(_gateway!));

        await using (host)
        {
            (await host.StartAsync(settings, CancellationToken.None)).Started.Should().BeTrue();
            host.IsListening.Should().BeTrue();

            await host.StopAsync(CancellationToken.None);
            host.IsListening.Should().BeFalse();

            var again = await host.StartAsync(settings, CancellationToken.None);

            again.Started.Should().BeTrue(again.Detail);
            host.IsListening.Should().BeTrue();
            host.Address.Should().Be($"http://127.0.0.1:{settings.McpPort}/mcp");
        }
    }

    [Fact]
    public void TheRegistrationCommandIsTheOneAnAgentPastes()
    {
        McpServerHost.RegistrationCommand("127.0.0.1", 8765, string.Empty)
            .Should().Be("claude mcp add --transport http bettertranslator http://127.0.0.1:8765/mcp");

        McpServerHost.RegistrationCommand("192.0.2.10", 9000, "secret")
            .Should().Contain("http://192.0.2.10:9000/mcp")
            .And.Contain("Authorization: Bearer secret");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}
