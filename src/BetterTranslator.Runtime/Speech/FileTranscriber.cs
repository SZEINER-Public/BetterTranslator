using System.IO;

namespace BetterTranslator.Runtime.Speech;

public enum TranscriptionFault
{
    None,
    InputMissing,
    Unavailable,
    InvalidRequest,
    Failed,
    Cancelled,
}

public sealed record TranscribedSegment(
    int Segment,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string Raw,
    float MinConfidence,
    IReadOnlyList<DictationSubstitution> Substitutions);

public sealed record Transcription
{
    public required bool Ok { get; init; }

    public TranscriptionFault Fault { get; init; }

    public string? Error { get; init; }

    public required string Path { get; init; }

    public required string Language { get; init; }

    public DictationMode Mode { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<TranscribedSegment> Segments { get; init; } = [];

    public RecognitionStats? Stats { get; init; }
}

public static class FileTranscriber
{
    public static async Task<Transcription> TranscribeAsync(
        string path,
        string language,
        DictationMode mode,
        VocabularySources? vocabulary = null,
        IEnumerable<string>? harvestTexts = null,
        IntPtr recognizer = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(language);

        var full = System.IO.Path.GetFullPath(path);
        Transcription Fail(TranscriptionFault fault, string message) => new()
        {
            Ok = false,
            Fault = fault,
            Error = message,
            Path = full,
            Language = language,
            Mode = mode,
        };

        if (string.IsNullOrWhiteSpace(language))
        {
            return Fail(TranscriptionFault.InvalidRequest, "A language is required, as a code such as en or cs.");
        }

        if (!File.Exists(full))
        {
            return Fail(TranscriptionFault.InputMissing, $"There is no file at {full}.");
        }

        if (!string.Equals(System.IO.Path.GetExtension(full), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(TranscriptionFault.InvalidRequest, $"{full} is not a .wav file. Transcription reads PCM or float WAV only.");
        }

        var availability = SpeechSupport.ProbeRecognition(recognizer);

        if (!availability.CanRecognize)
        {
            return Fail(TranscriptionFault.Unavailable, availability.Reason);
        }

        try
        {
            using var lexicon = TechnicalVocabulary.Build(vocabulary ?? new VocabularySources(), harvestTexts: harvestTexts);
            using var engine = RecognitionEngine.Create(recognizer, lexicon);

            if (!engine.Capabilities.Available)
            {
                return Fail(TranscriptionFault.Unavailable, engine.UnavailableReason);
            }

            if (engine.Languages.Count > 0 && !engine.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                return Fail(
                    TranscriptionFault.InvalidRequest,
                    $"The recognition model does not list '{language}'. It lists {string.Join(", ", engine.Languages)}.");
            }

            var canonical = engine.Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase))
                ?? language;
            var rate = engine.Capabilities.SampleRate;

            using var session = engine.Start(new RecognitionRequest
            {
                Language = canonical,
                Mode = mode,
                FilePath = full,
                PartialIntervalMs = 0,
            });

            using var registration = cancellationToken.Register(session.Cancel);
            var segments = new SortedDictionary<int, List<RecognitionEvent>>();
            RecognitionEvent? finished = null;

            while (finished is null)
            {
                if (!session.TryPoll(out var e))
                {
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                switch (e.Kind)
                {
                    case RecognitionEventKind.Final:
                        if (!segments.TryGetValue(e.Segment, out var list))
                        {
                            segments[e.Segment] = list = [];
                        }

                        list.Add(e);
                        break;

                    case RecognitionEventKind.Finished:
                        finished = e;
                        break;
                }
            }

            var stats = session.Stats;

            if (finished.Status == SpeechStatus.Cancelled)
            {
                return Fail(TranscriptionFault.Cancelled, "The transcription was cancelled.") with { Stats = stats };
            }

            if (finished.Status != SpeechStatus.Ok)
            {
                return Fail(TranscriptionFault.Failed, string.IsNullOrEmpty(finished.Text) ? session.Error : finished.Text)
                    with { Stats = stats };
            }

            var result = segments.Select(pair => Segment(pair.Key, pair.Value, rate)).ToList();

            return new Transcription
            {
                Ok = true,
                Path = full,
                Language = canonical,
                Mode = mode,
                Text = string.Join('\n', result.Select(s => s.Text).Where(t => t.Length > 0)),
                Segments = result,
                Stats = stats,
            };
        }
        catch (SpeechException ex) when (ex.Status is SpeechStatus.Asset or SpeechStatus.Io)
        {
            return Fail(TranscriptionFault.InputMissing, ex.Message);
        }
        catch (SpeechException ex) when (ex.Status is SpeechStatus.InvalidArgument)
        {
            return Fail(TranscriptionFault.InvalidRequest, ex.Message);
        }
        catch (SpeechException ex) when (ex.Status is SpeechStatus.Unavailable)
        {
            return Fail(TranscriptionFault.Unavailable, ex.Message);
        }
        catch (SpeechException ex)
        {
            return Fail(TranscriptionFault.Failed, ex.Message);
        }
    }

    private static TranscribedSegment Segment(int index, List<RecognitionEvent> finals, int rate)
    {
        var substitutions = new List<DictationSubstitution>();
        var text = string.Empty;
        var raw = string.Empty;

        foreach (var final in finals)
        {
            var textShift = text.Length == 0 || final.Text.Length == 0 ? text.Length : text.Length + 1;
            var rawShift = raw.Length == 0 || final.Raw.Length == 0 ? raw.Length : raw.Length + 1;

            substitutions.AddRange(final.Substitutions.Select(s => s with
            {
                TextStart = s.TextStart + textShift,
                RawStart = s.RawStart + rawShift,
            }));

            text = Join(text, final.Text);
            raw = Join(raw, final.Raw);
        }

        var start = finals.Count == 0 ? 0 : finals[0].StartSample;
        var end = finals.Count == 0 ? 0 : finals[^1].EndSample;
        var lowest = finals.Count == 0 ? 1f : finals.Min(f => f.MinConfidence);

        return new TranscribedSegment(index, Time(start, rate), Time(end, rate), text, raw, lowest, substitutions);
    }

    private static string Join(string left, string right) =>
        left.Length == 0 ? right : right.Length == 0 ? left : left + " " + right;

    private static TimeSpan Time(long samples, int rate) =>
        rate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)samples / rate);
}
