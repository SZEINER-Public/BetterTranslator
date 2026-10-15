using System.Runtime.InteropServices;

namespace BetterTranslator.Runtime.Speech;

public enum DictationMode
{
    Prose = 0,
    Code = 1,
}

public enum SubstitutionKind
{
    Casing = 0,
    Lexicon = 1,
    Grammar = 2,
    Letters = 3,
}

public enum RecognitionEventKind
{
    None = 0,
    Partial = 1,
    Final = 2,
    Finished = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsSubstitution
{
    public uint StructSize;
    public int Kind;
    public int Applied;
    public float Confidence;
    public nuint RawOffset;
    public nuint RawLength;
    public nuint TextOffset;
    public nuint TextLength;
    public IntPtr Replacement;
    public nuint ReplacementLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsVadSettings
{
    public uint StructSize;
    public float OnsetDb;
    public float AbsoluteFloorDbfs;
    public float MinSpeechMs;
    public float HangoverMs;
    public float PreRollMs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsRecEngineConfig
{
    public uint StructSize;
    public IntPtr Recognizer;
    public IntPtr Lexicon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsRecCapabilities
{
    public uint StructSize;
    public int Available;
    public int SampleRate;
    public int MaxWindowSamples;
    public int Streaming;
    public int WordTimestamps;
    public int BiasContext;
    public int BiasLogit;
    public int BiasTrie;
    public int LanguageCount;
    public int LexiconTerms;
    public int Capture;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsRecRequest
{
    public uint StructSize;
    public IntPtr Language;
    public int Mode;
    public int Threads;
    public IntPtr FilePath;
    public int InputSampleRate;
    public int InputRingSamples;
    public int WindowMs;
    public int OverlapMs;
    public int PartialIntervalMs;
    public float MinConfidence;
    public int ContextChars;
    public int BiasTerms;
    public BsVadSettings Vad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsRecEvent
{
    public uint StructSize;
    public int Kind;
    public int Segment;
    public int SegmentEnd;
    public int Status;
    public int Substitutions;
    public long StartSample;
    public long EndSample;
    public float MinConfidence;
    public IntPtr Text;
    public nuint TextLength;
    public IntPtr Unstable;
    public nuint UnstableLength;
    public IntPtr Raw;
    public nuint RawLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsRecStats
{
    public uint StructSize;
    public int Finished;
    public int Status;
    public int ModelSampleRate;
    public int Segments;
    public int Finals;
    public int Partials;
    public int Windows;
    public long InputFrames;
    public long OverrunFrames;
    public long ModelSamples;
    public long PeakWindowSamples;
    public long FirstVoicedMicros;
    public long FirstPartialMicros;
    public long FirstFinalMicros;
    public long FinishedMicros;
    public long SpeechEndToFinalCount;
    public long SpeechEndToFinalMedianMicros;
    public long SpeechEndToFinalP90Micros;
    public long OnsetToFirstPartialMicros;
    public long FeedMicros;
    public long PartialMicros;
    public long FinalizeMicros;
    public long CancelLatencyMicros;
}

internal sealed class LexiconHandle : SafeHandle
{
    public LexiconHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        RecognitionNative.bs_lexicon_free(handle);
        return true;
    }
}

internal sealed class DictationHandle : SafeHandle
{
    public DictationHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        RecognitionNative.bs_dictation_free(handle);
        return true;
    }
}

internal sealed class RecognitionEngineHandle : SafeHandle
{
    public RecognitionEngineHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        RecognitionNative.bs_rec_engine_free(handle);
        return true;
    }
}

internal sealed class RecognitionSessionHandle : SafeHandle
{
    public RecognitionSessionHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        RecognitionNative.bs_rec_session_free(handle);
        return true;
    }
}

internal sealed class CaptureHandle : SafeHandle
{
    public CaptureHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        RecognitionNative.bs_capture_close(handle);
        return true;
    }
}

internal static class RecognitionNative
{
    private const string Lib = SpeechNative.Lib;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_stt_abi_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_lexicon_create(int maxTerms, out LexiconHandle lexicon);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_lexicon_add(LexiconHandle lexicon, IntPtr term, nuint termLength, float weight);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_lexicon_harvest(LexiconHandle lexicon, IntPtr text, nuint textLength);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_lexicon_freeze(LexiconHandle lexicon);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_lexicon_count(LexiconHandle lexicon);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_lexicon_term(LexiconHandle lexicon, int index, byte[]? buffer, int capacity,
                                               out int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong bs_lexicon_version(LexiconHandle lexicon);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_lexicon_free(IntPtr lexicon);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_dictate(IntPtr text, nuint textLength, int mode, IntPtr lexicon,
                                          float minConfidence, out DictationHandle dictation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_dictation_text(DictationHandle dictation, out IntPtr text, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_dictation_raw(DictationHandle dictation, out IntPtr text, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_dictation_substitution_count(DictationHandle dictation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_dictation_substitution(DictationHandle dictation, int index,
                                                         ref BsSubstitution substitution);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_dictation_free(IntPtr dictation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_vad_defaults(ref BsVadSettings settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_vad_calibrate([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                ref BsVadSettings settings, out float noiseDbfs,
                                                out float speechDbfs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_engine_create(ref BsRecEngineConfig config,
                                                    out RecognitionEngineHandle engine);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_engine_capabilities(RecognitionEngineHandle engine,
                                                          ref BsRecCapabilities capabilities);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_engine_unavailable_reason(RecognitionEngineHandle engine, byte[]? buffer,
                                                                int capacity, out int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_engine_language(RecognitionEngineHandle engine, int index, byte[]? buffer,
                                                      int capacity, out int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_rec_engine_free(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_request_defaults(ref BsRecRequest request);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_start(RecognitionEngineHandle engine, ref BsRecRequest request,
                                                    out RecognitionSessionHandle session);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_push(RecognitionSessionHandle session, ref float interleaved,
                                                   int frames, int channels);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_rec_session_end_input(RecognitionSessionHandle session);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_rec_session_cancel(RecognitionSessionHandle session);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_poll(RecognitionSessionHandle session, ref BsRecEvent recognitionEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_substitution(RecognitionSessionHandle session, int index,
                                                           ref BsSubstitution substitution);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_wait(RecognitionSessionHandle session, int timeoutMilliseconds,
                                                   out int status);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_stats(RecognitionSessionHandle session, ref BsRecStats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_rec_session_error(RecognitionSessionHandle session, byte[]? buffer, int capacity,
                                                    out int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_rec_session_free(IntPtr session);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_capture_device_count(out int count);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_capture_device(int index, byte[]? id, int idCapacity, out int idLength,
                                                 byte[]? name, int nameCapacity, out int nameLength);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_capture_open([MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceId,
                                               out CaptureHandle capture);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_capture_format(CaptureHandle capture, out int sampleRate, out int channels);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_capture_start(CaptureHandle capture, RecognitionSessionHandle session);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_capture_stats(CaptureHandle capture, out long frames, out long glitches);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_capture_close(IntPtr capture);
}
