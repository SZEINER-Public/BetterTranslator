using System.IO;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class HostedInferenceTests
{
    [Fact]
    public void TheHostShipsBesideTheApplication()
    {
        HostedSession.HostPath.Should().EndWith("BetterTranslator.Host.exe");
        File.Exists(HostedSession.HostPath).Should().BeTrue(
            "the host is copied next to the binary that starts it, so a packaging slip is a test failure");
        HostedSession.HostExists.Should().BeTrue();
    }

    [Fact]
    public void HostedInferenceIsOnByDefault() => LocalTranslator.UseHost.Should().BeTrue();

    [Fact]
    public void ARequestSurvivesTheRoundTrip()
    {
        var request = new HostRequest
        {
            Op = "complete",
            Text = "Line one\nline two with \"quotes\" and a tab\there.",
            MaxTokens = 512,
            Temperature = 0.2f,
            TopP = 0.95f,
            TopK = 40,
            RepeatPenalty = 1f,
            Seed = 7,
        };

        var line = HostProtocol.Encode(request);

        line.Should().NotContain("\n").And.NotContain("\r");

        var back = HostProtocol.Decode<HostRequest>(line);

        back.Should().NotBeNull();
        back!.Text.Should().Be(request.Text);
        back.MaxTokens.Should().Be(512);
        back.Seed.Should().Be(7u);
        back.Temperature.Should().Be(0.2f);
    }

    [Fact]
    public void AResponseSurvivesTheRoundTrip()
    {
        var response = new HostResponse { Ok = true, Text = "Výchozí pojmenování výstupu", Tokens = 12 };

        var back = HostProtocol.Decode<HostResponse>(HostProtocol.Encode(response));

        back.Should().NotBeNull();
        back!.Ok.Should().BeTrue();
        back.Text.Should().Be("Výchozí pojmenování výstupu", "accented answers must cross the pipe intact");
        back.Tokens.Should().Be(12);
    }

    [Fact]
    public void AFailureCarriesItsReason()
    {
        var failed = HostResponse.Failed("the model file is missing");

        failed.Ok.Should().BeFalse();
        failed.Error.Should().Be("the model file is missing");

        HostProtocol.Decode<HostResponse>(HostProtocol.Encode(failed))!.Error
            .Should().Be("the model file is missing");
    }

    [Fact]
    public void AHostFailureReadsAsPlainWordsRatherThanAnErrorCode()
    {
        new BetterRuntimeException("the model runtime crashed while generating")
            .Message.Should().Be("the model runtime crashed while generating");

        new BetterRuntimeException(BrStatus.ModelLoad, "no such file")
            .Message.Should().Contain("ModelLoad", "a real native status still names itself");
    }

    [Fact]
    public void AGenerationReportsFailureRatherThanRaisingIt()
    {
        var faulted = Completion.Failed("the model runtime crashed while generating");

        faulted.Faulted.Should().BeTrue();
        faulted.Fault.Should().Be("the model runtime crashed while generating");
        faulted.Text.Should().BeEmpty();
        faulted.Tokens.Should().Be(0);

        var answered = new Completion("Sestavení je zelené.", 9, null);

        answered.Faulted.Should().BeFalse();
        answered.Text.Should().Be("Sestavení je zelené.");
    }

    [Fact]
    public void TheSeamReturnsACompletionRatherThanATuple()
    {
        typeof(IInferenceSession).GetMethod(nameof(IInferenceSession.CompleteCounted))!
            .ReturnType.Should().Be<Completion>();
    }

    [Fact]
    public void TheSessionSeamCarriesLiveness()
    {
        typeof(IInferenceSession).GetProperty(nameof(IInferenceSession.IsAlive))
            .Should().NotBeNull();

        typeof(IInferenceSession).Should().BeAssignableTo<System.IDisposable>();
    }
}
