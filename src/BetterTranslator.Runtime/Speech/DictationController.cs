namespace BetterTranslator.Runtime.Speech;

public sealed record DictationOptions
{
    public required string Language { get; init; }

    public DictationMode Mode { get; init; } = DictationMode.Prose;

    public string? MicrophoneId { get; init; }

    public VocabularySources Vocabulary { get; init; } = new();

    public IReadOnlyList<string> ExtraTerms { get; init; } = [];

    public IReadOnlyList<string> HarvestTexts { get; init; } = [];

    public int Threads { get; init; }
}

public sealed record DictationCommit(string Text, string Raw, IReadOnlyList<DictationSubstitution> Substitutions, bool SegmentEnd);

public sealed record DictationStopped(SpeechStatus Status, string Message);

public sealed class DictationController : IDisposable
{
    private readonly IntPtr _recognizer;
    private readonly object _gate = new();
    private SpeechLexicon? _lexicon;
    private RecognitionEngine? _engine;
    private RecognitionSession? _session;
    private MicrophoneCapture? _microphone;
    private Task? _pump;
    private SynchronizationContext? _context;
    private bool _cancelled;

    public DictationController(IntPtr recognizer = default)
    {
        _recognizer = recognizer;
        Availability = SpeechSupport.ProbeRecognition(recognizer);
    }

    public RecognitionAvailability Availability { get; }

    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public event EventHandler<string>? ProvisionalChanged;

    public event EventHandler<DictationCommit>? Committed;

    public event EventHandler<DictationStopped>? Stopped;

    public void Start(DictationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Availability.CanRecognize)
        {
            throw new InvalidOperationException(Availability.Reason);
        }

        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("Dictation is already listening.");
            }

            _context = SynchronizationContext.Current;
            _cancelled = false;

            try
            {
                _lexicon = TechnicalVocabulary.Build(options.Vocabulary, options.ExtraTerms, options.HarvestTexts);
                _engine = RecognitionEngine.Create(_recognizer, _lexicon);

                if (!_engine.Capabilities.Available)
                {
                    throw new InvalidOperationException(_engine.UnavailableReason);
                }

                _microphone = MicrophoneCapture.Open(options.MicrophoneId);
                _session = _engine.Start(new RecognitionRequest
                {
                    Language = options.Language,
                    Mode = options.Mode,
                    Threads = options.Threads,
                    InputSampleRate = _microphone.SampleRate,
                    InputRingSamples = _microphone.SampleRate * 10,
                });
                _microphone.Start(_session);
            }
            catch
            {
                Release();
                throw;
            }

            var session = _session;
            _pump = Task.Run(() => PumpAsync(session));
        }
    }

    public async Task StopAsync()
    {
        Task? pump;

        lock (_gate)
        {
            _microphone?.Dispose();
            _microphone = null;
            _session?.EndInput();
            pump = _pump;
        }

        if (pump is not null)
        {
            await pump.ConfigureAwait(false);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cancelled = true;
            _microphone?.Dispose();
            _microphone = null;
            _session?.Cancel();
        }
    }

    public void Dispose()
    {
        Cancel();

        try
        {
            _pump?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            Release();
        }
    }

    private async Task PumpAsync(RecognitionSession session)
    {
        var provisional = string.Empty;
        DictationStopped stopped;

        try
        {
            while (true)
            {
                if (!session.TryPoll(out var e))
                {
                    await Task.Delay(15).ConfigureAwait(false);
                    continue;
                }

                if (e.Kind == RecognitionEventKind.Partial)
                {
                    var text = e.Unstable.Length == 0 ? e.Text : e.Text.Length == 0 ? e.Unstable : e.Text + " " + e.Unstable;

                    if (!string.Equals(text, provisional, StringComparison.Ordinal))
                    {
                        provisional = text;
                        Raise(() => ProvisionalChanged?.Invoke(this, text));
                    }

                    continue;
                }

                if (e.Kind == RecognitionEventKind.Final)
                {
                    var cancelled = false;

                    lock (_gate)
                    {
                        cancelled = _cancelled;
                    }

                    if (!cancelled && e.Text.Length > 0)
                    {
                        var commit = new DictationCommit(e.Text, e.Raw, e.Substitutions, e.SegmentEnd);
                        Raise(() => Committed?.Invoke(this, commit));
                    }

                    provisional = string.Empty;
                    Raise(() => ProvisionalChanged?.Invoke(this, string.Empty));
                    continue;
                }

                if (e.Kind == RecognitionEventKind.Finished)
                {
                    stopped = new DictationStopped(e.Status, e.Status == SpeechStatus.Ok ? string.Empty : e.Text);
                    break;
                }
            }
        }
        catch (SpeechException ex)
        {
            stopped = new DictationStopped(ex.Status, ex.Message);
        }

        lock (_gate)
        {
            Release();
        }

        Raise(() =>
        {
            ProvisionalChanged?.Invoke(this, string.Empty);
            Stopped?.Invoke(this, stopped);
        });
    }

    private void Raise(Action action)
    {
        var context = _context;

        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }

    private void Release()
    {
        _microphone?.Dispose();
        _microphone = null;
        _session?.Dispose();
        _session = null;
        _engine?.Dispose();
        _engine = null;
        _lexicon?.Dispose();
        _lexicon = null;
        _pump = null;
    }
}
