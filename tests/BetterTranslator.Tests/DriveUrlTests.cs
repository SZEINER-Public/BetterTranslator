using System.IO;
using System.Linq;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The Drive branch of the link parser, and the manifest it resolves names
/// against. Everything here is offline: none of it opens a socket.
/// </summary>
public sealed class DriveUrlTests
{
    private const string KnownId = "1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo";

    [Theory]
    [InlineData("https://drive.google.com/file/d/1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo/view?usp=sharing")]
    [InlineData("https://drive.google.com/open?id=1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo")]
    [InlineData("https://drive.usercontent.google.com/download?id=1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo&export=download&confirm=t")]
    [InlineData("1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo")]
    public void EveryShapePeopleActuallyPasteNormalisesToTheConfirmedDownloadUrl(string pasted)
    {
        var result = ModelUrlParser.Parse(pasted);

        result.IsAccepted.Should().BeTrue();
        result.Kind.Should().Be(ModelUrlKind.GoogleDriveFile);

        // export=download with confirm=t is what skips the virus-scan
        // interstitial without OAuth. Losing either turns the response into an
        // HTML page with status 200.
        result.DownloadUrl!.ToString().Should().Be(
            "https://drive.usercontent.google.com/download?id=" + KnownId + "&export=download&confirm=t");
    }

    [Fact]
    public void AKnownIdIsNamedFromTheManifestRatherThanLeftAsAnId()
    {
        ModelUrlParser.Parse(KnownId).ModelName.Should().Be("gemma3-1b-Q4_K_M");
    }

    [Fact]
    public void AnUnknownIdIsStillUsableEvenThoughItsNameIsNotKnownYet()
    {
        var result = ModelUrlParser.Parse("1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        result.IsAccepted.Should().BeTrue();
        result.ModelName.Should().Be("1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    }

    [Fact]
    public void AFolderLinkIsRefusedByNameRatherThanCalledInvalid()
    {
        var result = ModelUrlParser.Parse("https://drive.google.com/drive/folders/1abcdefghijklmnopqrstuvwxyz012345");

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Contain("folder");
    }

    [Fact]
    public void ADriveLinkWithNoFileIdSaysSo()
    {
        var result = ModelUrlParser.Parse("https://drive.google.com/");

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Contain("file id");
    }

    [Fact]
    public void TheExistingRejectionsStillHold()
    {
        // The Drive branch runs before the extension check, so this guards
        // against it swallowing links it has no business accepting.
        ModelUrlParser.Parse("https://example.com/model.zip").IsAccepted.Should().BeFalse();
        ModelUrlParser.Parse("https://huggingface.co/owner/model").IsAccepted.Should().BeTrue();
        ModelUrlParser.Parse("https://example.com/model.gguf").IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void TheManifestCoversEveryArtifactAndAgreesWithTheCatalogue()
    {
        // Eleven model artifacts, all three runtime DLLs, and the two cuBLAS
        // libraries the CUDA runtime imports and cannot load without.
        ArtifactManifest.All.Should().HaveCount(16);
        ArtifactManifest.All.Count(a => a.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .Should().Be(11);
        ArtifactManifest.ForFileName("BetterRuntimeCPU.dll")!.SizeBytes.Should().Be(3595264);
        ArtifactManifest.ForFileName("BetterRuntimeVulkan.dll")!.SizeBytes.Should().Be(54225408);
        ArtifactManifest.ForFileName("BetterRuntimeCUDA.dll")!.SizeBytes.Should().Be(145155584);

        // Every runtime flavour with a link needs an integrity row too, or a
        // download of it would be checked on length alone.
        foreach (var runtime in ComponentCatalog
            .RuntimesFor(new HardwareReport(true, true, CanUseVulkan: true, CanUseCuda: true))
            .Where(r => r.DownloadUrl is not null))
        {
            ArtifactManifest.ForFileName(runtime.FileName)!.SizeBytes
                .Should().Be(runtime.SizeBytes, $"{runtime.Name}'s catalogue size must match the measured one");
        }
        ArtifactManifest.All.Select(a => a.FileId).Should().OnlyHaveUniqueItems();
        ArtifactManifest.All.Should().AllSatisfy(a => a.SizeBytes.Should().BeGreaterThan(0));

        // Every catalogue row that claims a download must be an artifact the
        // manifest can verify, or nothing could check what arrived.
        foreach (var component in ComponentCatalog.BuiltIn.Where(c => c.DownloadUrl is not null))
        {
            ArtifactManifest.ForFileName(component.FileName)
                .Should().NotBeNull($"{component.Name} has a download link, so it needs an integrity row");

            ArtifactManifest.ForFileName(component.FileName)!.SizeBytes
                .Should().Be(component.SizeBytes, $"{component.Name}'s catalogue size must match the measured one");
        }
    }

    [Fact]
    public void AnArtifactWithNoPublishedChecksumIsReportedUnverifiedRatherThanPassed()
    {
        // Drive publishes no checksum and no ETag, so most rows carry no hash.
        // The distinction that matters: length and structure checked, contents
        // not, said out loud rather than reported as a clean pass.
        var known = ArtifactManifest.For("1wp9dMol3rKUX2NeES-UHwXvDwHMqAZxo")!;
        known.Sha256.Should().BeNull();

        var file = Path.Combine(Path.GetTempPath(), "bt-verify-" + Guid.NewGuid().ToString("N") + ".gguf");

        try
        {
            var body = new byte[known.SizeBytes > 4096 ? 4096 : known.SizeBytes];
            body[0] = 0x47; body[1] = 0x47; body[2] = 0x55; body[3] = 0x46; body[4] = 3;
            File.WriteAllBytes(file, body);

            var shortened = known with { SizeBytes = body.Length };
            var verdict = ArtifactManifest.Verify(file, shortened, actualSha256: null);

            verdict.Passed.Should().BeTrue();
            verdict.Detail.Should().Contain("unverified");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
