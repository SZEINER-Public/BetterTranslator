using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BetterTranslator.Cli;
using BetterTranslator.Runtime.Agents;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

public sealed class CliContractTests(ITestOutputHelper output)
{
    [Fact]
    public void TextTargetAndSourceAreParsedFromTheArguments()
    {
        var command = CommandLine.Parse(["translate", "The build is green.", "--from", "en", "--to", "cs"]);

        command.IsValid.Should().BeTrue(command.Error);
        command.Verb.Should().Be(Verb.Translate);
        command.Text.Should().Be("The build is green.");
        command.From.Should().Be("en");
        command.To.Should().Be("cs");
        command.Json.Should().BeFalse();
    }

    [Fact]
    public void TranslateWithNoTextIsTheStdinPath()
    {
        var command = CommandLine.Parse(["translate", "--from", "en", "--to", "cs"]);

        command.IsValid.Should().BeTrue(command.Error);
        command.Text.Should().BeNull("no text on the command line means the text arrives on stdin");
        command.File.Should().BeNull();
        command.Batch.Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { "translate", "hi" }, "--to")]
    [InlineData(new[] { "translate", "--file", "a.md", "--batch", "b", "--to", "cs" }, "not both")]
    [InlineData(new[] { "translate", "hi", "--file", "a.md", "--to", "cs" }, "not more than one")]
    [InlineData(new[] { "translate", "hi", "--to", "cs", "--context-dir", "." }, "--context-dir")]
    [InlineData(new[] { "nonsense" }, "not a command")]
    [InlineData(new[] { "translate", "hi", "--to", "cs", "--nope" }, "not an option")]
    public void AnArgumentMistakeIsRefusedWithAReasonThatNamesIt(string[] args, string expected)
    {
        var command = CommandLine.Parse(args);

        command.IsValid.Should().BeFalse();
        command.Error.Should().Contain(expected);
    }

    [Fact]
    public void EveryFaultMapsToTheDocumentedExitCode()
    {
        ExitCode.For(AgentFault.None).Should().Be(0);
        ExitCode.For(AgentFault.UnknownLanguage).Should().Be(2);
        ExitCode.For(AgentFault.SameLanguage).Should().Be(2);
        ExitCode.For(AgentFault.InputMissing).Should().Be(3);
        ExitCode.For(AgentFault.ModelMissing).Should().Be(4);
        ExitCode.For(AgentFault.RuntimeUnreachable).Should().Be(4);
        ExitCode.For(AgentFault.TranslationFailed).Should().Be(5);

        ExitCode.For("input_missing").Should().Be(3);
        ExitCode.For("runtime_unreachable").Should().Be(4);
        ExitCode.For("translation_failed").Should().Be(5);
    }

    [Fact]
    public void TheTextEnvelopeCarriesTheDocumentedKeys()
    {
        var json = Envelope.Text(new TextTranslation("en", "cs", "EuroLLM", "Sestaveni je zelene.", 7, 120, null));

        output.WriteLine(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().Should().BeTrue();
        root.GetProperty("from").GetString().Should().Be("en");
        root.GetProperty("to").GetString().Should().Be("cs");
        root.GetProperty("model").GetString().Should().Be("EuroLLM");
        root.GetProperty("result").GetString().Should().Be("Sestaveni je zelene.");
    }

    [Fact]
    public void TheFileEnvelopeCarriesOneResultPerFile()
    {
        var json = Envelope.Files(
        [
            FileTranslation.Done(@"C:\docs\a.md", @"C:\docs\a.cs.md"),
            FileTranslation.Failed(@"C:\docs\b.pdf", new AgentError(AgentFault.UnsupportedFormat, "binary")),
        ]);

        output.WriteLine(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().Should().BeFalse("one of the files failed");

        var results = root.GetProperty("results");
        results.GetArrayLength().Should().Be(2);

        results[0].GetProperty("file").GetString().Should().Be(@"C:\docs\a.md");
        results[0].GetProperty("status").GetString().Should().Be("ok");
        results[0].GetProperty("out").GetString().Should().Be(@"C:\docs\a.cs.md");
        results[0].GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);

        results[1].GetProperty("status").GetString().Should().Be("failed");
        results[1].GetProperty("error").GetString().Should().Be("binary");

        var error = root.GetProperty("error");

        error.GetProperty("code").GetInt32().Should().Be(2, "an unsupported format is an argument error");
        error.GetProperty("message").GetString().Should().Contain("b.pdf");
    }

    [Fact]
    public void AnOkFalseEnvelopeAlwaysCarriesAnErrorObject()
    {
        foreach (var json in new[]
                 {
                     Envelope.Failure(4, "no runtime"),
                     Envelope.Files([FileTranslation.Failed("a.md", new AgentError(AgentFault.InputMissing, "gone"))]),
                 })
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            root.GetProperty("ok").GetBoolean().Should().BeFalse();
            root.TryGetProperty("error", out var error).Should().BeTrue("one failure shape, whatever the command");
            error.GetProperty("code").GetInt32().Should().BeGreaterThan(0);
            error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void TheFailureEnvelopeCarriesTheExitCodeAndTheMessage()
    {
        var json = Envelope.Failure(3, "There is no file at C:\\missing.md.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(3);
        root.GetProperty("error").GetProperty("message").GetString().Should().Contain("missing.md");
    }

    [Fact]
    public void OutputNamingPutsTheTargetCodeBeforeTheExtension()
    {
        DocumentFormats.OutputPathFor(@"C:\docs\readme.md", "cs", null)
            .Should().Be(@"C:\docs\readme.cs.md");

        DocumentFormats.OutputPathFor(@"C:\docs\readme.md", "cs", @"D:\out\hand-picked.md")
            .Should().Be(@"D:\out\hand-picked.md");
    }

    [Fact]
    public void ThePipeServerOffersTheSameTenToolsAndRefusesJsonOnStdout()
    {
        var command = CommandLine.Parse(["mcp"]);

        command.IsValid.Should().BeTrue(command.Error);
        command.Verb.Should().Be(Verb.Mcp);

        CommandLine.Parse(["mcp", "--json"]).Error
            .Should().Contain("--json", "stdout carries the protocol in this mode");

        Help.Mcp.Should().Contain("claude mcp add -s user bettertranslator");
        Help.Mcp.Should().Contain("full path", "a bare bt only resolves when it is on PATH");
        Help.Root.Should().Contain("mcp");
    }

    [Fact]
    public void ThePipeServerNamesTheBinaryThatAnsweredBeforeItServes()
    {
        var banner = StdioServer.Banner();

        output.WriteLine(banner);

        banner.Should().StartWith("bettertranslator mcp ");
        banner.Should().MatchRegex(@"\d+\.\d+\.\d+, built \d{4}-\d{2}-\d{2} \d{2}:\d{2}, from ");
        banner.Should().Contain(Path.GetFileName(Environment.ProcessPath!));

        Help.Mcp.Should().Contain("stale", "the build time is there to catch a binary older than the feature");
    }

    [Fact]
    public void HelpNamesEveryCommandTheExitCodesAndAnExample()
    {
        Help.Root.Should().Contain("bt translate").And.Contain("bt languages").And.Contain("bt models");
        Help.Root.Should().Contain("EXIT CODES").And.Contain("3  input file missing");

        foreach (var verb in new[] { Verb.Translate, Verb.Languages, Verb.Models })
        {
            var text = Help.For(verb);

            text.Should().Contain("USAGE");
            text.Should().Contain("EXAMPLES");
            text.Should().Contain("EXIT CODES");
        }
    }

    [Fact]
    public async Task HelpPrintsOnStdoutAndExitsZero()
    {
        var run = await RunAsync("--help");

        run.Exit.Should().Be(0);
        run.Stdout.Should().Contain("bt <command>");
        run.Stderr.Should().BeEmpty("stdout carries the answer and stderr stays quiet on success");
    }

    [Fact]
    public async Task AnUnknownCommandExitsTwoAndSaysSoOnStderr()
    {
        var run = await RunAsync("nonsense");

        run.Exit.Should().Be(2);
        run.Stdout.Should().BeEmpty("stdout carries only the result");
        run.Stderr.Should().Contain("not a command");
    }

    [Fact]
    public async Task AFailureInJsonModeIsAnEnvelopeOnStdoutAndNothingOnStderr()
    {
        var run = await RunAsync("nonsense", "--json");

        run.Exit.Should().Be(2);
        run.Stderr.Should().BeEmpty();

        using var document = JsonDocument.Parse(run.Stdout);

        document.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(2);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "bt.exe");

        File.Exists(exe).Should().BeTrue($"the command line must be built beside the tests: {exe}");

        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;

        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
