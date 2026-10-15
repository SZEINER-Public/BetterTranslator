using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace BetterTranslator.Runtime.Speech;

public sealed record DictationSubstitution(
    SubstitutionKind Kind,
    bool Applied,
    float Confidence,
    int RawStart,
    int RawLength,
    int TextStart,
    int TextLength,
    string Replacement);

public sealed record DictationResult(string Text, string Raw, IReadOnlyList<DictationSubstitution> Substitutions);

public sealed record VoiceActivitySettings(
    float OnsetDb = 12f,
    float AbsoluteFloorDbfs = -50f,
    float MinSpeechMs = 100f,
    float HangoverMs = 450f,
    float PreRollMs = 200f);

public sealed record RecognitionRequest
{
    public required string Language { get; init; }

    public DictationMode Mode { get; init; } = DictationMode.Prose;

    public int Threads { get; init; }

    public string? FilePath { get; init; }

    public int InputSampleRate { get; init; }

    public int InputRingSamples { get; init; } = 48000 * 10;

    public int WindowMs { get; init; }

    public int OverlapMs { get; init; } = 1000;

    public int PartialIntervalMs { get; init; } = 500;

    public float MinConfidence { get; init; } = 0.6f;

    public int ContextChars { get; init; } = 200;

    public int BiasTerms { get; init; } = 64;

    public VoiceActivitySettings VoiceActivity { get; init; } = new();
}

public sealed record RecognitionCapabilities(
    bool Available,
    int SampleRate,
    int MaxWindowSamples,
    bool Streaming,
    bool WordTimestamps,
    bool BiasContext,
    bool BiasLogit,
    bool BiasTrie,
    int LanguageCount,
    int LexiconTerms,
    bool Capture);

public sealed record RecognitionEvent(
    RecognitionEventKind Kind,
    int Segment,
    bool SegmentEnd,
    SpeechStatus Status,
    long StartSample,
    long EndSample,
    float MinConfidence,
    string Text,
    string Unstable,
    string Raw,
    IReadOnlyList<DictationSubstitution> Substitutions);

public sealed record RecognitionStats(
    bool Finished,
    SpeechStatus Status,
    int ModelSampleRate,
    int Segments,
    int Finals,
    int Partials,
    int Windows,
    long InputFrames,
    long OverrunFrames,
    long ModelSamples,
    long PeakWindowSamples,
    TimeSpan? FirstVoiced,
    TimeSpan? FirstPartial,
    TimeSpan? FirstFinal,
    TimeSpan? FinishedAt,
    long SpeechEndToFinalCount,
    TimeSpan? SpeechEndToFinalMedian,
    TimeSpan? SpeechEndToFinalP90,
    TimeSpan? OnsetToFirstPartial,
    TimeSpan Feed,
    TimeSpan Partial,
    TimeSpan Finalize,
    TimeSpan? CancelLatency)
{
    internal static RecognitionStats From(in BsRecStats s) => new(
        s.Finished != 0,
        (SpeechStatus)s.Status,
        s.ModelSampleRate,
        s.Segments,
        s.Finals,
        s.Partials,
        s.Windows,
        s.InputFrames,
        s.OverrunFrames,
        s.ModelSamples,
        s.PeakWindowSamples,
        Optional(s.FirstVoicedMicros),
        Optional(s.FirstPartialMicros),
        Optional(s.FirstFinalMicros),
        Optional(s.FinishedMicros),
        s.SpeechEndToFinalCount,
        Optional(s.SpeechEndToFinalMedianMicros),
        Optional(s.SpeechEndToFinalP90Micros),
        Optional(s.OnsetToFirstPartialMicros),
        Micros(s.FeedMicros),
        Micros(s.PartialMicros),
        Micros(s.FinalizeMicros),
        Optional(s.CancelLatencyMicros));

    private static TimeSpan Micros(long value) => TimeSpan.FromTicks(value * TimeSpan.TicksPerMicrosecond);

    private static TimeSpan? Optional(long value) => value < 0 ? null : Micros(value);
}

public sealed record Microphone(string Id, string Name);

internal static class NativeUtf8
{
    internal static byte[] Bytes(IntPtr pointer, nuint length)
    {
        if (pointer == IntPtr.Zero || length == 0)
        {
            return [];
        }

        var bytes = new byte[checked((int)length)];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static int Chars(byte[] utf8, nuint byteOffset)
    {
        var offset = (int)Math.Min((ulong)byteOffset, (ulong)utf8.Length);
        return Encoding.UTF8.GetCharCount(utf8, 0, offset);
    }

    internal static DictationSubstitution Substitution(in BsSubstitution s, byte[] raw, byte[] text)
    {
        var rawStart = Chars(raw, s.RawOffset);
        var rawEnd = Chars(raw, s.RawOffset + s.RawLength);
        var textStart = Chars(text, s.TextOffset);
        var textEnd = Chars(text, s.TextOffset + s.TextLength);
        var replacement = Encoding.UTF8.GetString(Bytes(s.Replacement, s.ReplacementLength));

        return new DictationSubstitution(
            (SubstitutionKind)s.Kind,
            s.Applied != 0,
            s.Confidence,
            rawStart,
            rawEnd - rawStart,
            textStart,
            textEnd - textStart,
            replacement);
    }
}

public sealed class SpeechLexicon : IDisposable
{
    public const int MaxTerms = 65536;

    private readonly LexiconHandle _handle;

    private SpeechLexicon(LexiconHandle handle) => _handle = handle;

    public bool IsFrozen { get; private set; }

    public int Count => RecognitionNative.bs_lexicon_count(_handle);

    public ulong Version => RecognitionNative.bs_lexicon_version(_handle);

    internal LexiconHandle Handle => _handle;

    public static SpeechLexicon Create(int maxTerms = 8192)
    {
        RecognitionEngine.EnsureAbi();
        var status = RecognitionNative.bs_lexicon_create(maxTerms, out var handle);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            handle.Dispose();
            throw failure;
        }

        return new SpeechLexicon(handle);
    }

    public bool Add(string term, float weight = 1f)
    {
        ArgumentNullException.ThrowIfNull(term);
        using var text = new Utf8Text(term);
        var status = RecognitionNative.bs_lexicon_add(_handle, text.Pointer, (nuint)text.Length, weight);

        if (status == (int)SpeechStatus.State)
        {
            throw SpeechException.FromLast(status);
        }

        return status == 0;
    }

    public void Harvest(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return;
        }

        using var utf8 = new Utf8Text(text);
        var status = RecognitionNative.bs_lexicon_harvest(_handle, utf8.Pointer, (nuint)utf8.Length);

        if (status != 0)
        {
            throw SpeechException.FromLast(status);
        }
    }

    public void Freeze()
    {
        var status = RecognitionNative.bs_lexicon_freeze(_handle);

        if (status != 0)
        {
            throw SpeechException.FromLast(status);
        }

        IsFrozen = true;
    }

    public IReadOnlyList<string> Terms()
    {
        var count = Count;
        var terms = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            terms.Add(NativeText.Read((byte[]? buffer, int capacity, out int length) =>
                RecognitionNative.bs_lexicon_term(_handle, index, buffer, capacity, out length)));
        }

        return terms;
    }

    public void Dispose() => _handle.Dispose();
}

public static class Dictation
{
    public static DictationResult Apply(string spoken, DictationMode mode, SpeechLexicon? lexicon = null,
                                        float minConfidence = 0.6f)
    {
        ArgumentNullException.ThrowIfNull(spoken);
        RecognitionEngine.EnsureAbi();

        if (lexicon is { IsFrozen: false })
        {
            throw new InvalidOperationException("Freeze the lexicon before dictating with it.");
        }

        using var text = new Utf8Text(spoken);
        var added = false;

        try
        {
            lexicon?.Handle.DangerousAddRef(ref added);
            var pointer = lexicon is null ? IntPtr.Zero : lexicon.Handle.DangerousGetHandle();
            var status = RecognitionNative.bs_dictate(text.Pointer, (nuint)text.Length, (int)mode, pointer,
                                                      minConfidence, out var handle);

            if (status != 0)
            {
                var failure = SpeechException.FromLast(status);
                handle.Dispose();
                throw failure;
            }

            using (handle)
            {
                RecognitionNative.bs_dictation_text(handle, out var textPointer, out var textLength);
                RecognitionNative.bs_dictation_raw(handle, out var rawPointer, out var rawLength);
                var textBytes = NativeUtf8.Bytes(textPointer, textLength);
                var rawBytes = NativeUtf8.Bytes(rawPointer, rawLength);
                var count = RecognitionNative.bs_dictation_substitution_count(handle);
                var substitutions = new List<DictationSubstitution>(count);

                for (var i = 0; i < count; i++)
                {
                    var s = new BsSubstitution { StructSize = (uint)Marshal.SizeOf<BsSubstitution>() };

                    if (RecognitionNative.bs_dictation_substitution(handle, i, ref s) == 0)
                    {
                        substitutions.Add(NativeUtf8.Substitution(s, rawBytes, textBytes));
                    }
                }

                return new DictationResult(Encoding.UTF8.GetString(textBytes), Encoding.UTF8.GetString(rawBytes),
                                           substitutions);
            }
        }
        finally
        {
            if (added)
            {
                lexicon!.Handle.DangerousRelease();
            }
        }
    }
}

public sealed class RecognitionEngine : IDisposable
{
    public const int BoundAbi = 1;

    private readonly RecognitionEngineHandle _handle;

    private RecognitionEngine(RecognitionEngineHandle handle)
    {
        _handle = handle;
        Capabilities = ReadCapabilities(handle);
        UnavailableReason = NativeText.Read((byte[]? buffer, int capacity, out int length) =>
            RecognitionNative.bs_rec_engine_unavailable_reason(handle, buffer, capacity, out length));
        Languages = ReadLanguages(handle, Capabilities.LanguageCount);
    }

    public RecognitionCapabilities Capabilities { get; }

    public string UnavailableReason { get; }

    public IReadOnlyList<string> Languages { get; }

    public static RecognitionEngine Create(IntPtr recognizer = default, SpeechLexicon? lexicon = null)
    {
        EnsureAbi();

        if (lexicon is { IsFrozen: false })
        {
            throw new InvalidOperationException("Freeze the lexicon before an engine uses it.");
        }

        var added = false;

        try
        {
            lexicon?.Handle.DangerousAddRef(ref added);

            var config = new BsRecEngineConfig
            {
                StructSize = (uint)Marshal.SizeOf<BsRecEngineConfig>(),
                Recognizer = recognizer,
                Lexicon = lexicon is null ? IntPtr.Zero : lexicon.Handle.DangerousGetHandle(),
            };

            var status = RecognitionNative.bs_rec_engine_create(ref config, out var handle);

            if (status != 0)
            {
                var failure = SpeechException.FromLast(status);
                handle.Dispose();
                throw failure;
            }

            return new RecognitionEngine(handle);
        }
        finally
        {
            if (added)
            {
                lexicon!.Handle.DangerousRelease();
            }
        }
    }

    public RecognitionSession Start(RecognitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Language);

        using var language = new Utf8Text(request.Language);
        using var file = new Utf8Text(string.IsNullOrEmpty(request.FilePath) ? null : request.FilePath);
        var vad = request.VoiceActivity;

        var native = new BsRecRequest
        {
            StructSize = (uint)Marshal.SizeOf<BsRecRequest>(),
            Language = language.Pointer,
            Mode = (int)request.Mode,
            Threads = request.Threads,
            FilePath = file.Pointer,
            InputSampleRate = request.InputSampleRate,
            InputRingSamples = request.InputRingSamples,
            WindowMs = request.WindowMs,
            OverlapMs = request.OverlapMs,
            PartialIntervalMs = request.PartialIntervalMs,
            MinConfidence = request.MinConfidence,
            ContextChars = request.ContextChars,
            BiasTerms = request.BiasTerms,
            Vad = new BsVadSettings
            {
                StructSize = (uint)Marshal.SizeOf<BsVadSettings>(),
                OnsetDb = vad.OnsetDb,
                AbsoluteFloorDbfs = vad.AbsoluteFloorDbfs,
                MinSpeechMs = vad.MinSpeechMs,
                HangoverMs = vad.HangoverMs,
                PreRollMs = vad.PreRollMs,
            },
        };

        var status = RecognitionNative.bs_rec_session_start(_handle, ref native, out var session);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            session.Dispose();
            throw failure;
        }

        return new RecognitionSession(session);
    }

    public void Dispose() => _handle.Dispose();

    internal static void EnsureAbi()
    {
        SpeechEngine.EnsureAbi();
        var abi = RecognitionNative.bs_stt_abi_version();

        if (abi != BoundAbi)
        {
            throw new SpeechException(
                SpeechStatus.Internal,
                $"BetterSpeech reports recognition ABI {abi}; this build binds recognition ABI {BoundAbi}. Rebuild the native library and the application together.");
        }
    }

    private static RecognitionCapabilities ReadCapabilities(RecognitionEngineHandle handle)
    {
        var caps = new BsRecCapabilities { StructSize = (uint)Marshal.SizeOf<BsRecCapabilities>() };
        var status = RecognitionNative.bs_rec_engine_capabilities(handle, ref caps);

        if (status != 0)
        {
            throw SpeechException.FromLast(status);
        }

        return new RecognitionCapabilities(
            caps.Available != 0,
            caps.SampleRate,
            caps.MaxWindowSamples,
            caps.Streaming != 0,
            caps.WordTimestamps != 0,
            caps.BiasContext != 0,
            caps.BiasLogit != 0,
            caps.BiasTrie != 0,
            caps.LanguageCount,
            caps.LexiconTerms,
            caps.Capture != 0);
    }

    private static IReadOnlyList<string> ReadLanguages(RecognitionEngineHandle handle, int count)
    {
        var languages = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            languages.Add(NativeText.Read((byte[]? buffer, int capacity, out int length) =>
                RecognitionNative.bs_rec_engine_language(handle, index, buffer, capacity, out length)));
        }

        return languages;
    }
}

public sealed class RecognitionSession : IDisposable
{
    private readonly RecognitionSessionHandle _handle;
    private readonly object _poll = new();

    internal RecognitionSession(RecognitionSessionHandle handle) => _handle = handle;

    internal RecognitionSessionHandle Handle => _handle;

    public SpeechStatus Push(ReadOnlySpan<float> interleaved, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var frames = interleaved.Length / channels;

        if (frames == 0)
        {
            return SpeechStatus.Ok;
        }

        return (SpeechStatus)RecognitionNative.bs_rec_session_push(
            _handle, ref MemoryMarshal.GetReference(interleaved), frames, channels);
    }

    public void EndInput() => RecognitionNative.bs_rec_session_end_input(_handle);

    public void Cancel() => RecognitionNative.bs_rec_session_cancel(_handle);

    public bool TryPoll([NotNullWhen(true)] out RecognitionEvent? recognitionEvent)
    {
        lock (_poll)
        {
            var e = new BsRecEvent { StructSize = (uint)Marshal.SizeOf<BsRecEvent>() };
            var status = RecognitionNative.bs_rec_session_poll(_handle, ref e);

            if (status != 0)
            {
                throw SpeechException.FromLast(status);
            }

            if (e.Kind == (int)RecognitionEventKind.None)
            {
                recognitionEvent = null;
                return false;
            }

            var text = NativeUtf8.Bytes(e.Text, e.TextLength);
            var raw = NativeUtf8.Bytes(e.Raw, e.RawLength);
            var substitutions = new List<DictationSubstitution>(e.Substitutions);

            for (var i = 0; i < e.Substitutions; i++)
            {
                var s = new BsSubstitution { StructSize = (uint)Marshal.SizeOf<BsSubstitution>() };

                if (RecognitionNative.bs_rec_session_substitution(_handle, i, ref s) == 0)
                {
                    substitutions.Add(NativeUtf8.Substitution(s, raw, text));
                }
            }

            recognitionEvent = new RecognitionEvent(
                (RecognitionEventKind)e.Kind,
                e.Segment,
                e.SegmentEnd != 0,
                (SpeechStatus)e.Status,
                e.StartSample,
                e.EndSample,
                e.MinConfidence,
                Encoding.UTF8.GetString(text),
                Encoding.UTF8.GetString(NativeUtf8.Bytes(e.Unstable, e.UnstableLength)),
                Encoding.UTF8.GetString(raw),
                substitutions);
            return true;
        }
    }

    public SpeechStatus? Wait(TimeSpan timeout)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? -1
            : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

        var rc = RecognitionNative.bs_rec_session_wait(_handle, milliseconds, out var status);

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

    public RecognitionStats Stats
    {
        get
        {
            var stats = new BsRecStats { StructSize = (uint)Marshal.SizeOf<BsRecStats>() };
            var status = RecognitionNative.bs_rec_session_stats(_handle, ref stats);

            if (status != 0)
            {
                throw SpeechException.FromLast(status);
            }

            return RecognitionStats.From(stats);
        }
    }

    public string Error => NativeText.Read((byte[]? buffer, int capacity, out int length) =>
        RecognitionNative.bs_rec_session_error(_handle, buffer, capacity, out length));

    public void Dispose() => _handle.Dispose();
}

public sealed class MicrophoneCapture : IDisposable
{
    private readonly CaptureHandle _handle;
    private RecognitionSession? _session;
    private bool _sessionReferenced;

    private MicrophoneCapture(CaptureHandle handle)
    {
        _handle = handle;
        RecognitionNative.bs_capture_format(handle, out var rate, out var channels);
        SampleRate = rate;
        Channels = channels;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long Frames
    {
        get
        {
            RecognitionNative.bs_capture_stats(_handle, out var frames, out _);
            return frames;
        }
    }

    public long Glitches
    {
        get
        {
            RecognitionNative.bs_capture_stats(_handle, out _, out var glitches);
            return glitches;
        }
    }

    public static IReadOnlyList<Microphone> List()
    {
        RecognitionEngine.EnsureAbi();
        var status = RecognitionNative.bs_capture_device_count(out var count);

        if (status != 0)
        {
            throw SpeechException.FromLast(status);
        }

        var devices = new List<Microphone>(count);

        for (var i = 0; i < count; i++)
        {
            RecognitionNative.bs_capture_device(i, null, 0, out var idLength, null, 0, out var nameLength);
            var id = new byte[Math.Max(idLength, 1)];
            var name = new byte[Math.Max(nameLength, 1)];
            status = RecognitionNative.bs_capture_device(i, id, id.Length, out idLength, name, name.Length,
                                                         out nameLength);

            if (status == 0)
            {
                devices.Add(new Microphone(Encoding.UTF8.GetString(id, 0, idLength),
                                           Encoding.UTF8.GetString(name, 0, nameLength)));
            }
        }

        return devices;
    }

    public static MicrophoneCapture Open(string? deviceId = null)
    {
        RecognitionEngine.EnsureAbi();
        var status = RecognitionNative.bs_capture_open(string.IsNullOrEmpty(deviceId) ? null : deviceId,
                                                       out var handle);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            handle.Dispose();
            throw failure;
        }

        return new MicrophoneCapture(handle);
    }

    public void Start(RecognitionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_session is not null)
        {
            throw new InvalidOperationException("This microphone is already feeding a session.");
        }

        session.Handle.DangerousAddRef(ref _sessionReferenced);
        _session = session;
        var status = RecognitionNative.bs_capture_start(_handle, session.Handle);

        if (status != 0)
        {
            var failure = SpeechException.FromLast(status);
            ReleaseSession();
            throw failure;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        ReleaseSession();
    }

    private void ReleaseSession()
    {
        if (_sessionReferenced)
        {
            _session!.Handle.DangerousRelease();
            _sessionReferenced = false;
        }

        _session = null;
    }
}
