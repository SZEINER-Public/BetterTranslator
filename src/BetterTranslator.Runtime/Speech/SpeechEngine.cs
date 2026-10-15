using System.Runtime.InteropServices;
using System.Text;

namespace BetterTranslator.Runtime.Speech;

public sealed class SpeechException : Exception
{
    public SpeechException(SpeechStatus status, string message, long utf8Offset = -1)
        : base(message)
    {
        Status = status;
        Utf8Offset = utf8Offset;
    }

    public SpeechStatus Status { get; }

    /// <summary>
    /// The byte offset into the UTF-8 text the failure refers to, such as an unknown
    /// tag, or -1. A byte offset, not a character index.
    /// </summary>
    public long Utf8Offset { get; }

    /// <summary>Reads the calling thread's native error, which is only valid straight after the failing call.</summary>
    internal static SpeechException FromLast(int status)
    {
        var message = Marshal.PtrToStringUTF8(SpeechNative.bs_last_error());
        var offset = SpeechNative.bs_last_error_offset();

        return new SpeechException(
            (SpeechStatus)status,
            string.IsNullOrEmpty(message) ? $"BetterSpeech failed with {(SpeechStatus)status}" : message,
            offset);
    }
}

public sealed record VoiceSettings(string? VoiceId = null, float Rate = 1f, float PitchSemitones = 0f, float GainDb = 0f);

public sealed record SpeechRequest
{
    public required string Text { get; init; }

    /// <summary>BCP-47. Chooses the sentence rules and is handed to the model.</summary>
    public required string Language { get; init; }

    public VoiceSettings Voice { get; init; } = new();

    public UnknownTagPolicy UnknownTags { get; init; } = UnknownTagPolicy.Error;

    /// <summary>A smaller budget for the first chunk only, so the first audio is asked for sooner. 0 uses the model's limit.</summary>
    public int FirstChunkUnits { get; init; }

    /// <summary>The CPU share for the model. 0 lets it decide, which oversubscribes a machine already translating.</summary>
    public int Threads { get; init; }

    /// <summary>0 means no playback stream. Otherwise the bound on audio held ahead of the reader.</summary>
    public int PlaybackRingSamples { get; init; }

    public int PrebufferSamples { get; init; }

    /// <summary>Written as &lt;path&gt;.partial and renamed only when the job succeeds.</summary>
    public string? ExportWavPath { get; init; }

    /// <summary>Negative uses the engine default.</summary>
    public float BoundaryFadeMs { get; init; } = -1f;

    /// <summary>0 uses the engine default.</summary>
    public float AudibleThresholdDbfs { get; init; }
}

public sealed record SpeechCapabilities(
    bool SynthesisAvailable,
    int SampleRate,
    bool Streaming,
    int MinUnitSamples,
    int MaxUnitsPerChunk,
    int VoiceCount,
    SpeechControl Voice,
    SpeechControl Rate,
    SpeechControl Pitch,
    SpeechControl Gain,
    SpeechControl Emotion,
    SpeechControl Cloning,
    EventRender Laugh,
    int LaughClipSampleRate);

/// <summary>
/// One job's measured figures. Times are from the start call; null where the point
/// was not reached. FirstAudibleRead is when the first audible sample was handed to
/// the reader, a proxy for device submission, not for sound leaving a speaker.
/// </summary>
public sealed record SpeechStats(
    bool Finished,
    SpeechStatus Status,
    int SampleRate,
    int Chunks,
    int Events,
    EventRender LaughRender,
    long SamplesOut,
    long SamplesSource,
    long SamplesClip,
    long LimitedSamples,
    long Underruns,
    long UnderrunSamples,
    long PeakBufferedSamples,
    TimeSpan? Plan,
    TimeSpan? FirstSourcePcm,
    TimeSpan? FirstAudiblePcm,
    TimeSpan? Prebuffered,
    TimeSpan? FirstAudibleRead,
    TimeSpan? FinishedAt,
    TimeSpan Source,
    TimeSpan Dsp,
    TimeSpan Export,
    TimeSpan? CancelLatency)
{
    internal static SpeechStats From(in BsJobStats s) => new(
        s.Finished != 0,
        (SpeechStatus)s.Status,
        s.SampleRate,
        s.Chunks,
        s.Events,
        (EventRender)s.LaughRender,
        s.SamplesOut,
        s.SamplesSource,
        s.SamplesClip,
        s.LimitedSamples,
        s.Underruns,
        s.UnderrunSamples,
        s.PeakBufferedSamples,
        Optional(s.PlanMicros),
        Optional(s.FirstSourcePcmMicros),
        Optional(s.FirstAudiblePcmMicros),
        Optional(s.PrebufferedMicros),
        Optional(s.FirstAudibleReadMicros),
        Optional(s.FinishedMicros),
        Micros(s.SourceMicros),
        Micros(s.DspMicros),
        Micros(s.ExportMicros),
        Optional(s.CancelLatencyMicros));

    private static TimeSpan Micros(long value) => TimeSpan.FromTicks(value * TimeSpan.TicksPerMicrosecond);

    private static TimeSpan? Optional(long value) => value < 0 ? null : Micros(value);
}

public sealed record SpeechPlanItem(SpeechItemKind Kind, SpeechEvent Event, string Text, int Utf8Offset, int Utf8Length);

/// <summary>
/// The speech engine: BetterSpeech, the native library built from native/BetterSpeech.
/// It owns the text frontend, chunking, voice processing, buffering and export. The
/// acoustic model is a separate bs_source_v1 table that a model runtime provides;
/// without one the engine still plans and validates, and reports why it cannot speak.
/// </summary>
public sealed class SpeechEngine : IDisposable
{
    /// <summary>The BS_ABI_VERSION this binding was written against.</summary>
    public const int BoundAbi = 1;

    private readonly SpeechEngineHandle _handle;

    private SpeechEngine(SpeechEngineHandle handle)
    {
        _handle = handle;
        Capabilities = ReadCapabilities(handle);
        UnavailableReason = ReadReason(handle);
    }

    public static string Version => Marshal.PtrToStringUTF8(SpeechNative.bs_version()) ?? "unknown";

    public SpeechCapabilities Capabilities { get; }

    /// <summary>Why synthesis is unavailable, or empty when it is available.</summary>
    public string UnavailableReason { get; }

    /// <summary>
    /// acousticSource is a bs_source_v1* owned by the runtime that made it, which must
    /// outlive this engine and every job started from it. Zero gives an engine that
    /// cannot synthesize. A configured laughter clip that is missing or unreadable
    /// fails here, before any job relies on it.
    /// </summary>
    public static SpeechEngine Create(IntPtr acousticSource = default, string? laughClipPath = null)
    {
        EnsureAbi();

        using var clip = new Utf8Text(string.IsNullOrEmpty(laughClipPath) ? null : laughClipPath);

        var config = new BsEngineConfig
        {
            StructSize = (uint)Marshal.SizeOf<BsEngineConfig>(),
            Source = acousticSource,
            LaughClipPath = clip.Pointer,
        };

        var status = SpeechNative.bs_engine_create(ref config, out var handle);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            handle.Dispose();
            throw failure;
        }

        return new SpeechEngine(handle);
    }

    /// <summary>
    /// Starts a job on its own native thread and returns at once. One job per
    /// engine: a second start while one is still running fails with
    /// <see cref="SpeechStatus.State"/>.
    /// </summary>
    public SpeechJob Start(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        ArgumentNullException.ThrowIfNull(request.Language);

        using var text = new Utf8Text(request.Text);
        using var language = new Utf8Text(request.Language);
        using var voice = new Utf8Text(string.IsNullOrEmpty(request.Voice.VoiceId) ? null : request.Voice.VoiceId);
        using var export = new Utf8Text(string.IsNullOrEmpty(request.ExportWavPath) ? null : request.ExportWavPath);

        var native = new BsRequest
        {
            StructSize = (uint)Marshal.SizeOf<BsRequest>(),
            Text = text.Pointer,
            TextLength = (nuint)text.Length,
            Language = language.Pointer,
            Voice = new BsVoice
            {
                VoiceId = voice.Pointer,
                Rate = request.Voice.Rate,
                PitchSemitones = request.Voice.PitchSemitones,
                GainDb = request.Voice.GainDb,
            },
            UnknownTagPolicy = (int)request.UnknownTags,
            FirstChunkUnits = request.FirstChunkUnits,
            Threads = request.Threads,
            PlaybackRingSamples = request.PlaybackRingSamples,
            PrebufferSamples = request.PrebufferSamples,
            ExportWavPath = export.Pointer,
            BoundaryFadeMs = request.BoundaryFadeMs,
            AudibleThresholdDbfs = request.AudibleThresholdDbfs,
        };

        var status = SpeechNative.bs_job_start(_handle, ref native, out var job);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            job.Dispose();
            throw failure;
        }

        return new SpeechJob(job);
    }

    public void Dispose() => _handle.Dispose();

    internal static void EnsureAbi()
    {
        var abi = SpeechNative.bs_abi_version();

        if (abi != BoundAbi)
        {
            throw new SpeechException(
                SpeechStatus.Internal,
                $"BetterSpeech reports ABI {abi}; this build binds ABI {BoundAbi}. Rebuild the native library and the application together.");
        }
    }

    private static SpeechCapabilities ReadCapabilities(SpeechEngineHandle handle)
    {
        var caps = new BsCapabilities { StructSize = (uint)Marshal.SizeOf<BsCapabilities>() };
        var status = SpeechNative.bs_engine_capabilities(handle, ref caps);

        if (status != 0)
        {
            throw SpeechException.FromLast(status);
        }

        return new SpeechCapabilities(
            caps.SynthesisAvailable != 0,
            caps.SampleRate,
            caps.Streaming != 0,
            caps.MinUnitSamples,
            caps.MaxUnitsPerChunk,
            caps.VoiceCount,
            (SpeechControl)caps.Voice,
            (SpeechControl)caps.Rate,
            (SpeechControl)caps.Pitch,
            (SpeechControl)caps.Gain,
            (SpeechControl)caps.Emotion,
            (SpeechControl)caps.Cloning,
            (EventRender)caps.Laugh,
            caps.LaughClipSampleRate);
    }

    private static string ReadReason(SpeechEngineHandle handle) =>
        NativeText.Read((byte[]? buffer, int capacity, out int length) =>
            SpeechNative.bs_engine_unavailable_reason(handle, buffer, capacity, out length));
}

/// <summary>A running or finished synthesis job.</summary>
public sealed class SpeechJob : IDisposable
{
    private readonly SpeechJobHandle _handle;

    internal SpeechJob(SpeechJobHandle handle) => _handle = handle;

    /// <summary>
    /// For the audio callback: never blocks and never allocates. Fills the first
    /// <paramref name="count"/> samples of the buffer, padding with silence;
    /// <paramref name="real"/> is how many were generated audio.
    /// </summary>
    public SpeechReadFlags Read(float[] buffer, int count, out int real)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);

        var status = SpeechNative.bs_job_read(_handle, buffer, count, out real, out var flags);

        if (status != 0)
        {
            throw new SpeechException((SpeechStatus)status, "this job was started without a playback stream");
        }

        return (SpeechReadFlags)flags;
    }

    /// <summary>Returns at once. Buffered audio is dropped now; generation stops at the model's next cancellation point.</summary>
    public void Cancel() => SpeechNative.bs_job_cancel(_handle);

    /// <summary>The outcome, or null when the job is still running at the timeout.</summary>
    public SpeechStatus? Wait(TimeSpan timeout)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? -1
            : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

        var rc = SpeechNative.bs_job_wait(_handle, milliseconds, out var status);

        if (rc == (int)SpeechStatus.Timeout)
        {
            return null;
        }

        if (rc != 0)
        {
            throw SpeechException.FromLast(rc);
        }

        return (SpeechStatus)status;
    }

    /// <summary>
    /// Waits off the calling thread. A cancelled token cancels the job and still
    /// waits for it to stop, then returns <see cref="SpeechStatus.Cancelled"/>
    /// rather than throwing, so the caller always learns how the job ended.
    /// </summary>
    public async Task<SpeechStatus> WaitAsync(CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(Cancel);

        while (true)
        {
            var outcome = await Task
                .Run(() => Wait(TimeSpan.FromMilliseconds(100)), CancellationToken.None)
                .ConfigureAwait(false);

            if (outcome is { } finished)
            {
                return finished;
            }
        }
    }

    public SpeechStats Stats
    {
        get
        {
            var stats = new BsJobStats { StructSize = (uint)Marshal.SizeOf<BsJobStats>() };
            var status = SpeechNative.bs_job_stats_get(_handle, ref stats);

            if (status != 0)
            {
                throw SpeechException.FromLast(status);
            }

            return SpeechStats.From(stats);
        }
    }

    /// <summary>Why the job failed, or empty.</summary>
    public string Error => NativeText.Read((byte[]? buffer, int capacity, out int length) =>
        SpeechNative.bs_job_error(_handle, buffer, capacity, out length));

    /// <summary>Cancels the job if it is still running and waits for its thread.</summary>
    public void Dispose() => _handle.Dispose();
}

public static class SpeechPlan
{
    /// <summary>
    /// What a text becomes before any model sees it: speech chunks and events in
    /// order. Without a model the budget is in codepoints; 0 gives one chunk per
    /// sentence. For previewing and validating input, such as naming an unknown tag.
    /// </summary>
    public static IReadOnlyList<SpeechPlanItem> Build(
        string text,
        string language,
        UnknownTagPolicy unknownTags = UnknownTagPolicy.Error,
        int maxUnits = 0,
        int firstChunkUnits = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        SpeechEngine.EnsureAbi();

        using var utf8 = new Utf8Text(text);
        using var lang = new Utf8Text(language);

        var options = new BsPlanOptions
        {
            StructSize = (uint)Marshal.SizeOf<BsPlanOptions>(),
            Language = lang.Pointer,
            UnknownTagPolicy = (int)unknownTags,
            MaxUnits = maxUnits,
            FirstChunkUnits = firstChunkUnits,
        };

        var status = SpeechNative.bs_plan_create(utf8.Pointer, (nuint)utf8.Length, ref options, out var plan);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            plan.Dispose();
            throw failure;
        }

        using (plan)
        {
            var count = checked((int)SpeechNative.bs_plan_count(plan));
            var items = new List<SpeechPlanItem>(count);

            for (var i = 0; i < count; i++)
            {
                var rc = SpeechNative.bs_plan_item_get(plan, (nuint)i, out var item);

                if (rc != 0)
                {
                    throw SpeechException.FromLast(rc);
                }

                var itemText = item.TextLength == 0
                    ? string.Empty
                    : Marshal.PtrToStringUTF8(item.Text, checked((int)item.TextLength)) ?? string.Empty;

                items.Add(new SpeechPlanItem(
                    (SpeechItemKind)item.Kind,
                    (SpeechEvent)item.Event,
                    itemText,
                    checked((int)item.SourceOffset),
                    checked((int)item.SourceLength)));
            }

            return items;
        }
    }
}

internal static class NativeText
{
    internal delegate int Reader(byte[]? buffer, int capacity, out int length);

    /// <summary>
    /// Reads a string through the buffer-and-length contract: ask for the length,
    /// then fill a buffer of it. Retried because the text can grow in between, as a
    /// job's error does when it finishes.
    /// </summary>
    internal static string Read(Reader read)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            read(null, 0, out var length);

            if (length <= 0)
            {
                return string.Empty;
            }

            var buffer = new byte[length];
            var status = read(buffer, buffer.Length, out var written);

            if (status == (int)SpeechStatus.BufferTooSmall)
            {
                continue;
            }

            if (status != 0)
            {
                throw SpeechException.FromLast(status);
            }

            return Encoding.UTF8.GetString(buffer, 0, written);
        }

        throw new SpeechException(SpeechStatus.Internal, "a native string kept changing length while it was read");
    }
}
