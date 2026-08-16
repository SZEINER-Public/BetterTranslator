using BetterTranslator.Runtime.Downloads;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The Add another model field accepts a Hugging Face page, a direct .gguf or a
/// direct .safetensors link, and refuses anything else by name rather than
/// with a generic error.
/// </summary>
public sealed class ModelUrlParserTests
{
    [Theory]
    [InlineData("https://huggingface.co/utter-project/EuroLLM-9B")]
    [InlineData("huggingface.co/utter-project/EuroLLM-9B")]
    [InlineData("https://huggingface.co/utter-project/EuroLLM-9B/tree/main")]
    public void AHuggingFacePageIsAccepted(string link)
    {
        var result = ModelUrlParser.Parse(link);

        result.IsAccepted.Should().BeTrue();
        result.Kind.Should().Be(ModelUrlKind.HuggingFacePage);
        result.ModelName.Should().Be("utter-project/EuroLLM-9B");
    }

    [Fact]
    public void ADirectGgufLinkIsAccepted()
    {
        var result = ModelUrlParser.Parse("https://example.com/models/eurollm-q4.gguf");

        result.IsAccepted.Should().BeTrue();
        result.Kind.Should().Be(ModelUrlKind.GgufFile);
        result.ModelName.Should().Be("eurollm-q4");
    }

    [Fact]
    public void ADirectSafetensorsLinkIsAccepted()
    {
        var result = ModelUrlParser.Parse("https://example.com/models/translategemma.safetensors");

        result.IsAccepted.Should().BeTrue();
        result.Kind.Should().Be(ModelUrlKind.SafetensorsFile);
    }

    [Fact]
    public void AZipLinkIsRefusedByName()
    {
        var result = ModelUrlParser.Parse("https://example.com/models/weights.zip");

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Contain(".zip", "the refusal names what was pasted");
        result.Rejection.Should().Contain(".gguf", "and says what would work instead");
    }

    [Theory]
    [InlineData("https://example.com/models/weights.tar.gz", ".gz")]
    [InlineData("https://example.com/models/weights.bin", ".bin")]
    [InlineData("https://example.com/models/readme.pdf", ".pdf")]
    public void OtherFileTypesAreRefusedByTheirOwnExtension(string link, string extension) =>
        ModelUrlParser.Parse(link).Rejection.Should().Contain(extension);

    [Fact]
    public void AnUnknownHostWithNoFileIsRefusedByHost()
    {
        var result = ModelUrlParser.Parse("https://example.net/some/page");

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Contain("example.net");
    }

    [Fact]
    public void AHuggingFaceUserPageIsNotAModelPage()
    {
        var result = ModelUrlParser.Parse("https://huggingface.co/utter-project");

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Contain("owner/model");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingPastedIsSaidPlainly(string? link) =>
        ModelUrlParser.Parse(link).Rejection.Should().Be("Paste a link to a model first.");

    [Fact]
    public void ANonHttpSchemeIsRefused() =>
        ModelUrlParser.Parse("ftp://example.com/model.gguf").Rejection.Should().Contain("https");

    [Fact]
    public void GibberishIsRefusedWithoutThrowing() =>
        ModelUrlParser.Parse("not a link at all").IsAccepted.Should().BeFalse();
}
