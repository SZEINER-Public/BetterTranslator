using System.Runtime.InteropServices;

namespace BetterTranslator.Runtime.Speech;

/// <summary>Mirrors bs_status in native/BetterSpeech/include/better_speech.h.</summary>
public enum SpeechStatus
{
    Ok = 0,
    InvalidArgument = -1,
    Unavailable = -2,
    Asset = -3,
    Io = -4,
    Cancelled = -5,
    BufferTooSmall = -6,
    Source = -7,
    Timeout = -8,
    State = -9,
    OutOfMemory = -10,
    Internal = -99,
}

public enum UnknownTagPolicy
{
    /// <summary>An unrecognised [Tag] fails the request and names its position.</summary>
    Error = 0,

    /// <summary>An unrecognised [Tag] is spoken as text, brackets included.</summary>
    Literal = 1,
}

public enum SpeechItemKind
{
    Speech = 0,
    Event = 1,
}

public enum SpeechEvent
{
    None = 0,
    Laugh = 1,
}

/// <summary>Who provides a control, so no surface offers a setting that does nothing.</summary>
public enum SpeechControl
{
    Unavailable = 0,
    Model = 1,
    Engine = 2,
}

public enum EventRender
{
    Unavailable = 0,

    /// <summary>The acoustic model produces the event in its own voice.</summary>
    Native = 1,

    /// <summary>An approved local recording is inserted. It is not the model's voice.</summary>
    Clip = 2,
}

[Flags]
public enum SpeechReadFlags
{
    None = 0,
    Prebuffering = 1 << 0,
    Underrun = 1 << 1,
    Finished = 1 << 2,
    Cancelled = 1 << 3,
}

// The layouts below are pinned by static_asserts in native/BetterSpeech/src/abi.cpp
// and by SpeechBindingTests. Every struct is blittable: strings cross as pointers
// the caller allocates and frees, so nothing is marshalled behind the call.

[StructLayout(LayoutKind.Sequential)]
internal struct BsPlanOptions
{
    public uint StructSize;
    public IntPtr Language;
    public int UnknownTagPolicy;
    public int MaxUnits;
    public int FirstChunkUnits;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsPlanItem
{
    public int Kind;
    public int Event;
    public IntPtr Text;
    public nuint TextLength;
    public nuint SourceOffset;
    public nuint SourceLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsEngineConfig
{
    public uint StructSize;
    public IntPtr Source;
    public IntPtr LaughClipPath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsCapabilities
{
    public uint StructSize;
    public int SynthesisAvailable;
    public int SampleRate;
    public int Streaming;
    public int MinUnitSamples;
    public int MaxUnitsPerChunk;
    public int VoiceCount;
    public int Voice;
    public int Rate;
    public int Pitch;
    public int Gain;
    public int Emotion;
    public int Cloning;
    public int Laugh;
    public int LaughClipSampleRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsVoice
{
    public IntPtr VoiceId;
    public float Rate;
    public float PitchSemitones;
    public float GainDb;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsRequest
{
    public uint StructSize;
    public IntPtr Text;
    public nuint TextLength;
    public IntPtr Language;
    public BsVoice Voice;
    public int UnknownTagPolicy;
    public int FirstChunkUnits;
    public int Threads;
    public int PlaybackRingSamples;
    public int PrebufferSamples;
    public IntPtr ExportWavPath;
    public float BoundaryFadeMs;
    public float AudibleThresholdDbfs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsJobStats
{
    public uint StructSize;
    public int Finished;
    public int Status;
    public int SampleRate;
    public int Chunks;
    public int Events;
    public int LaughRender;
    public long SamplesOut;
    public long SamplesSource;
    public long SamplesClip;
    public long LimitedSamples;
    public long Underruns;
    public long UnderrunSamples;
    public long PeakBufferedSamples;
    public long PlanMicros;
    public long FirstSourcePcmMicros;
    public long FirstAudiblePcmMicros;
    public long PrebufferedMicros;
    public long FirstAudibleReadMicros;
    public long FinishedMicros;
    public long SourceMicros;
    public long DspMicros;
    public long ExportMicros;
    public long CancelLatencyMicros;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BsWavInfo
{
    public uint StructSize;
    public int SampleRate;
    public int Channels;
    public int BitsPerSample;
    public int IsFloat;
    public long Frames;
}

internal sealed class SpeechPlanHandle : SafeHandle
{
    public SpeechPlanHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        SpeechNative.bs_plan_free(handle);
        return true;
    }
}

internal sealed class SpeechEngineHandle : SafeHandle
{
    public SpeechEngineHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        SpeechNative.bs_engine_free(handle);
        return true;
    }
}

/// <summary>
/// A job handle. Being a SafeHandle is what makes freeing it safe against a
/// playback callback: the release waits until every bs_job_read already inside the
/// native call has returned, which the ABI requires and a bare IntPtr could not
/// guarantee. Releasing cancels the job and joins its thread, so it can take as
/// long as one cancellation point.
/// </summary>
internal sealed class SpeechJobHandle : SafeHandle
{
    public SpeechJobHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        SpeechNative.bs_job_free(handle);
        return true;
    }
}

internal static class SpeechNative
{
    // Resolved by the default probing: BetterSpeech.dll on Windows, beside the
    // assembly. The BetterRuntime resolver on this assembly answers only for its own
    // name and returns zero for this one.
    internal const string Lib = "BetterSpeech";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_abi_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr bs_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr bs_last_error();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long bs_last_error_offset();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_plan_create(IntPtr text, nuint textLength, ref BsPlanOptions options,
                                              out SpeechPlanHandle plan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint bs_plan_count(SpeechPlanHandle plan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_plan_item_get(SpeechPlanHandle plan, nuint index, out BsPlanItem item);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_plan_free(IntPtr plan);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_engine_create(ref BsEngineConfig config, out SpeechEngineHandle engine);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_engine_capabilities(SpeechEngineHandle engine, ref BsCapabilities caps);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_engine_unavailable_reason(SpeechEngineHandle engine, byte[]? buffer, int capacity,
                                                            out int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_engine_free(IntPtr engine);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_job_start(SpeechEngineHandle engine, ref BsRequest request, out SpeechJobHandle job);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_job_read(SpeechJobHandle job, float[] buffer, int capacity, out int real,
                                           out int flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_job_cancel(SpeechJobHandle job);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_job_wait(SpeechJobHandle job, int timeoutMilliseconds, out int status);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_job_stats_get(SpeechJobHandle job, ref BsJobStats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_job_error(SpeechJobHandle job, byte[]? buffer, int capacity, out int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void bs_job_free(IntPtr job);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bs_wav_probe([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref BsWavInfo info);
}

/// <summary>
/// A UTF-8 copy of a string in unmanaged memory, for the pointer fields of the
/// request structs. The native side copies what it needs before returning, so the
/// copy lives for one call.
/// </summary>
internal readonly struct Utf8Text : IDisposable
{
    public Utf8Text(string? value)
    {
        if (value is null)
        {
            Pointer = IntPtr.Zero;
            Length = 0;
            return;
        }

        Pointer = Marshal.StringToCoTaskMemUTF8(value);
        Length = System.Text.Encoding.UTF8.GetByteCount(value);
    }

    public IntPtr Pointer { get; }

    public int Length { get; }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(Pointer);
        }
    }
}
