using System.Diagnostics;
using System.IO;
using System.Linq;
using BetterTranslator.Packer;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class SingleFileFactAttribute : FactAttribute
{
    public const string Gate = "BETTERTRANSLATOR_SINGLEFILE_EXE";

    public SingleFileFactAttribute()
    {
        string? path = Environment.GetEnvironmentVariable(Gate);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip = $"Set {Gate} to the packed BetterTranslator.exe to run the single file checks.";
        }
    }
}

public sealed class SingleFileBootstrapTests
{
    private static string Artifact =>
        Environment.GetEnvironmentVariable(SingleFileFactAttribute.Gate)
        ?? throw new InvalidOperationException($"{SingleFileFactAttribute.Gate} is not set");

    [SingleFileFact]
    public void TheShippedExecutableCarriesAPayloadSectionThatMatchesItsFooter()
    {
        byte[] image = File.ReadAllBytes(Artifact);
        ContainerReader reader = ContainerReader.Open(ContainerReader.ExtractSection(image, ".btpay"));

        reader.Entries.Should().NotBeEmpty();
        reader.Entries.Should().Contain(entry => entry.PortablePath == "hostfxr.dll");
        reader.Entries.Should().Contain(entry => entry.PortablePath == "BetterTranslator.dll");
        reader.Entries.Should().Contain(entry => entry.PortablePath == "coreclr.dll");
        reader.Entries.Should().OnlyContain(entry => !entry.PortablePath.Contains(".."));
    }

    [SingleFileFact]
    public void ThePayloadCarriesNoDebugSymbolsAndNoApphost()
    {
        byte[] image = File.ReadAllBytes(Artifact);
        ContainerReader reader = ContainerReader.Open(ContainerReader.ExtractSection(image, ".btpay"));

        reader.Entries.Should().NotContain(entry => entry.PortablePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));

        reader.Entries.Should().NotContain(
            entry => entry.PortablePath.Equals("BetterTranslator.exe", StringComparison.OrdinalIgnoreCase),
            "the bootstrap starts BetterTranslator.dll through hostfxr and never launches an extracted "
            + "executable, so packing the apphost would only drop a runnable exe into the cache");
    }

    [SingleFileFact]
    public void TheReleaseDirectoryHoldsExactlyOneFile()
    {
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Path.GetFullPath(Artifact))!)
                 .Select(Path.GetFileName)
                 .Should().ContainSingle().Which.Should().Be(Path.GetFileName(Artifact));
    }

    [SingleFileFact]
    public void SelfTestPasses()
    {
        (int exitCode, string output) = RunBootstrap(Artifact, "--bt-selftest");

        exitCode.Should().Be((int)BtPayFormat.ExitSuccess, output);
        output.Should().Contain("selftest PASS");
    }

    [SingleFileFact]
    public void AnUnknownBootstrapFlagIsRefusedWithTheDocumentedExitCode()
    {
        (int exitCode, _) = RunBootstrap(Artifact, "--bt-not-a-flag");

        exitCode.Should().Be((int)BtPayFormat.ExitFlagUsage);
    }

    [SingleFileFact]
    public void ACorruptedPayloadIsRefusedWithTheDocumentedExitCode()
    {
        string corrupted = Path.Combine(Path.GetTempPath(), "bt-corrupt-" + Guid.NewGuid().ToString("n") + ".exe");
        try
        {
            byte[] image = File.ReadAllBytes(Artifact);
            uint sectionOffset = PortableExecutableReader.ReadSections(image)
                                                         .Single(section => section.Name == ".btpay").RawOffset;

            image[sectionOffset + BtPayFormat.HeaderBytes + 512] ^= 0xFF;
            File.WriteAllBytes(corrupted, image);

            (int exitCode, string output) = RunBootstrap(corrupted, "--bt-verify");

            exitCode.Should().Be((int)BtPayFormat.ExitPayloadHashMismatch);
            output.Should().Contain("verify FAILED");
        }
        finally
        {
            File.Delete(corrupted);
        }
    }

    private static (int ExitCode, string Output) RunBootstrap(string artifact, params string[] arguments)
    {
        var start = new ProcessStartInfo(artifact)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"{artifact} did not start");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }
}
