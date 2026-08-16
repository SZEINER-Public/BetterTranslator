using System.IO;
using System.Threading;
using BetterTranslator.App.Mcp;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

[Collection(EngineConfigCollection.Name)]
public sealed class McpSettingsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-mcpsettings", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

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
    public void TheAgentSectionIsReachableAndRendersTheCommandToPaste()
    {
        var settings = Settings();

        settings.IsAgentTab.Should().BeFalse("the screen opens on Memory");

        settings.ShowAgentCommand.Execute(null);

        settings.Tab.Should().Be(SettingsTab.Agent);
        settings.IsAgentTab.Should().BeTrue();
        settings.IsMemoryTab.Should().BeFalse("one section shows at a time");

        output.WriteLine(settings.McpCommand);

        settings.McpCommand.Should().Be(
            "claude mcp add --transport http bettertranslator http://127.0.0.1:8765/mcp");
        settings.McpStatus.Should().Be("Off.");
        settings.McpNeedsToken.Should().BeFalse("loopback needs none");

        settings.McpStdioCommand.Should().StartWith("claude mcp add -s user bettertranslator -- ")
            .And.EndWith(" mcp", "a client that speaks over a pipe needs no host and no port");
    }

    [Fact]
    public void ChangingTheAddressRewritesTheCommandAndAsksForATokenOffLoopback()
    {
        var settings = Settings();

        settings.ShowAgentCommand.Execute(null);

        settings.McpPort = "9100";
        settings.McpCommand.Should().Contain("http://127.0.0.1:9100/mcp");

        settings.McpHost = "0.0.0.0";

        settings.McpNeedsToken.Should().BeTrue("a bind off loopback reaches the network");
        settings.McpStatus.Should().Contain("token");
        settings.McpStatus.Should().Contain("127.0.0.1", "the status names the way back");

        settings.McpToken = "s3cret";

        settings.McpNeedsToken.Should().BeFalse();
        settings.McpCommand.Should().Contain("Authorization: Bearer s3cret");
    }

    [Fact]
    public void TheSwitchIsRememberedAcrossASession()
    {
        var settings = Settings();

        settings.ShowAgentCommand.Execute(null);
        settings.McpEnabled = true;
        settings.McpPort = "9200";

        var reopened = Settings();

        reopened.McpEnabled.Should().BeTrue();
        reopened.McpPort.Should().Be("9200");
    }

    private SettingsViewModel Settings()
    {
        Directory.CreateDirectory(_root);

        var paths = new AppPaths(_root);
        var database = new Database(paths);

        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        var settings = new SettingsViewModel(
            new SettingsStore(database),
            new CacheInspector(paths, database),
            new InstallPaths(paths),
            _ => Task.CompletedTask,
            () => { },
            () => Task.CompletedTask,
            () => { });

        settings.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

        return settings;
    }
}
