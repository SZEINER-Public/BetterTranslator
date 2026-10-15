using System.IO;
using System.Runtime.InteropServices;
using BetterTranslator.Runtime.Speech;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Skips unless BetterSpeech.dll loads beside the test host. Decided at discovery,
/// like <see cref="ModelFactAttribute"/>, because xunit 2.x fixes Skip there.
/// </summary>
public sealed class SpeechNativeFactAttribute : FactAttribute
{
    private static readonly bool Loadable = Probe();

    public SpeechNativeFactAttribute()
    {
        if (!Loadable)
        {
            Skip = "BetterSpeech.dll is not beside the test host. Build native/BetterSpeech and pass -p:BetterSpeechDir=<folder>.";
        }
    }

    private static bool Probe()
    {
        try
        {
            return SpeechNative.bs_abi_version() == SpeechEngine.BoundAbi;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}

public sealed class SpeechBindingTests
{
    /// <summary>
    /// The same figures abi.cpp asserts on the native side. A struct that drifts on
    /// either side fails one of the two builds instead of corrupting a call.
    /// </summary>
    [Fact]
    public void ManagedLayoutsMatchTheNativeAbi()
    {
        Marshal.SizeOf<BsPlanOptions>().Should().Be(32);
        Marshal.SizeOf<BsPlanItem>().Should().Be(40);
        Marshal.SizeOf<BsEngineConfig>().Should().Be(24);
        Marshal.SizeOf<BsCapabilities>().Should().Be(60);
        Marshal.SizeOf<BsVoice>().Should().Be(24);
        Marshal.SizeOf<BsRequest>().Should().Be(96);
        Marshal.SizeOf<BsJobStats>().Should().Be(168);
        Marshal.SizeOf<BsWavInfo>().Should().Be(32);

        Marshal.OffsetOf<BsPlanOptions>(nameof(BsPlanOptions.UnknownTagPolicy)).ToInt32().Should().Be(16);
        Marshal.OffsetOf<BsPlanItem>(nameof(BsPlanItem.SourceLength)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<BsRequest>(nameof(BsRequest.Voice)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<BsRequest>(nameof(BsRequest.UnknownTagPolicy)).ToInt32().Should().Be(56);
        Marshal.OffsetOf<BsRequest>(nameof(BsRequest.ExportWavPath)).ToInt32().Should().Be(80);
        Marshal.OffsetOf<BsRequest>(nameof(BsRequest.AudibleThresholdDbfs)).ToInt32().Should().Be(92);
        Marshal.OffsetOf<BsJobStats>(nameof(BsJobStats.SamplesOut)).ToInt32().Should().Be(32);
        Marshal.OffsetOf<BsJobStats>(nameof(BsJobStats.CancelLatencyMicros)).ToInt32().Should().Be(160);
        Marshal.OffsetOf<BsWavInfo>(nameof(BsWavInfo.Frames)).ToInt32().Should().Be(24);
    }

    [SpeechNativeFact]
    public void ThePlanPutsTheLaughBetweenItsSentences()
    {
        var items = SpeechPlan.Build("Hello. [Laugh] That was unexpected.", "en");

        items.Should().HaveCount(3);
        items[0].Should().Be(new SpeechPlanItem(SpeechItemKind.Speech, SpeechEvent.None, "Hello.", 0, 6));
        items[1].Kind.Should().Be(SpeechItemKind.Event);
        items[1].Event.Should().Be(SpeechEvent.Laugh);
        items[2].Text.Should().Be("That was unexpected.");
    }

    [SpeechNativeFact]
    public void AnUnknownTagIsRefusedWithItsPosition()
    {
        var act = () => SpeechPlan.Build("Hi [Wink] there.", "en");

        var thrown = act.Should().Throw<SpeechException>().Which;
        thrown.Status.Should().Be(SpeechStatus.InvalidArgument);
        thrown.Utf8Offset.Should().Be(3);
        thrown.Message.Should().Contain("[Wink]");

        SpeechPlan.Build("Hi [Wink] there.", "en", UnknownTagPolicy.Literal)
            .Should().ContainSingle().Which.Text.Should().Be("Hi [Wink] there.");
    }

    [SpeechNativeFact]
    public void OffsetsAreUtf8Bytes()
    {
        // "Řekl" is five bytes in UTF-8 and four UTF-16 units; the plan reports bytes.
        var items = SpeechPlan.Build("Řekl. [Laugh]", "cs");

        items[0].Utf8Length.Should().Be(6);
        items[1].Utf8Offset.Should().Be(7);
    }

    [SpeechNativeFact]
    public void AnEngineWithoutAModelSaysSoInsteadOfSpeaking()
    {
        using var engine = SpeechEngine.Create();

        engine.Capabilities.SynthesisAvailable.Should().BeFalse();
        engine.Capabilities.Voice.Should().Be(SpeechControl.Unavailable);
        engine.Capabilities.Laugh.Should().Be(EventRender.Unavailable);
        engine.UnavailableReason.Should().NotBeNullOrWhiteSpace();

        var target = Path.Combine(Path.GetTempPath(), "bt-speech-never-written.wav");
        File.Delete(target);

        var act = () => engine.Start(new SpeechRequest { Text = "Hello.", Language = "en", ExportWavPath = target });

        act.Should().Throw<SpeechException>().Which.Status.Should().Be(SpeechStatus.Unavailable);
        File.Exists(target).Should().BeFalse();
        File.Exists(target + ".partial").Should().BeFalse();
    }

    [SpeechNativeFact]
    public void AMissingLaughterClipFailsAtCreation()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bt-speech-no-such-laugh.wav");
        File.Delete(missing);

        var act = () => SpeechEngine.Create(IntPtr.Zero, missing);

        act.Should().Throw<SpeechException>().Which.Status.Should().Be(SpeechStatus.Asset);
    }
}
