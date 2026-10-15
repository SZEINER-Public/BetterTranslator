namespace BetterTranslator.Runtime.Speech;

public sealed record SpeechAvailability(bool CanSpeak, string Reason);

public sealed record RecognitionAvailability(bool CanRecognize, bool EngineLoaded, string Reason);

/// <summary>
/// Whether this installation can speak, and if not, the first thing that stops it.
/// Answered in the order a person would have to fix things, so the reason shown is
/// the one worth acting on.
/// </summary>
public static class SpeechSupport
{
    /// <summary>
    /// The state of every build so far. The engine is here; what feeds it is not.
    /// BetterRuntime's ABI is text in, text out: no tokenizer, no embeddings and no
    /// audio. A speech model also has to be installed, and none is in the catalog.
    /// </summary>
    public const string NoAcousticModel =
        "Speech needs an acoustic model, and none can be attached yet. BetterRuntime, the llama.cpp runtime this "
        + "build binds, exports no speech entry points, and no speech-capable model is installed.";

    public static SpeechAvailability Probe()
    {
        try
        {
            SpeechEngine.EnsureAbi();
        }
        catch (DllNotFoundException)
        {
            return new SpeechAvailability(false, "The speech engine, BetterSpeech.dll, is not installed beside the application.");
        }
        catch (EntryPointNotFoundException)
        {
            return new SpeechAvailability(false, "BetterSpeech.dll is present but is not the speech engine this build expects.");
        }
        catch (BadImageFormatException)
        {
            return new SpeechAvailability(false, "BetterSpeech.dll is present but was built for a different processor.");
        }
        catch (SpeechException mismatch)
        {
            return new SpeechAvailability(false, mismatch.Message);
        }

        return new SpeechAvailability(false, NoAcousticModel);
    }

    public const string NoRecognitionModel =
        "Speech recognition needs a recognition model, and none can be attached yet. BetterRuntime, the llama.cpp "
        + "runtime this build binds, exports no audio entry points, and no recognition model is installed.";

    public static RecognitionAvailability ProbeRecognition(IntPtr recognizer = default)
    {
        try
        {
            RecognitionEngine.EnsureAbi();
        }
        catch (DllNotFoundException)
        {
            return new RecognitionAvailability(false, false, "The speech engine, BetterSpeech.dll, is not installed beside the application.");
        }
        catch (EntryPointNotFoundException)
        {
            return new RecognitionAvailability(false, false, "BetterSpeech.dll is present but has no speech recognition entry points.");
        }
        catch (BadImageFormatException)
        {
            return new RecognitionAvailability(false, false, "BetterSpeech.dll is present but was built for a different processor.");
        }
        catch (SpeechException mismatch)
        {
            return new RecognitionAvailability(false, false, mismatch.Message);
        }

        return recognizer == IntPtr.Zero
            ? new RecognitionAvailability(false, true, NoRecognitionModel)
            : new RecognitionAvailability(true, true, string.Empty);
    }
}
