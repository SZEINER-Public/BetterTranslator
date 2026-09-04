using System.Globalization;
using System.Text;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Core.Verification.Checks.Naturalness;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Core.Verification.Checks.Terminology;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Engine.Verification.Structure;

namespace BetterTranslator.Engine.Verification.Naturalness;

public sealed record RewriteRequest(
    string SourceSentence,
    string TargetSentence,
    string PreviousSentence,
    string NextSentence,
    IReadOnlyList<string> GlossaryLines,
    string RegisterProfile,
    IReadOnlyList<string> Findings,
    string TargetLanguage)
{
    public string Prompt()
    {
        var builder = new StringBuilder();
        builder.Append(PromptFragments.Current.SystemPrompt(TargetLanguage));
        builder.Append(" Restructure the target sentence so it reads as natural ").Append(TargetLanguage).Append(" while keeping every meaning, every term and every placeholder. Reply with one sentence and nothing else.\n");
        builder.Append("Source sentence: ").Append(SourceSentence).Append('\n');
        builder.Append("Current target sentence: ").Append(TargetSentence).Append('\n');

        if (PreviousSentence.Length > 0)
        {
            builder.Append("Previous target sentence: ").Append(PreviousSentence).Append('\n');
        }

        if (NextSentence.Length > 0)
        {
            builder.Append("Next target sentence: ").Append(NextSentence).Append('\n');
        }

        if (GlossaryLines.Count > 0)
        {
            builder.Append("Required terms: ").Append(string.Join("; ", GlossaryLines)).Append('\n');
        }

        if (RegisterProfile.Length > 0)
        {
            builder.Append("Document register: ").Append(RegisterProfile).Append('\n');
        }

        foreach (var finding in Findings)
        {
            builder.Append("Issue: ").Append(finding).Append('\n');
        }

        return builder.ToString();
    }
}

public sealed record RewriteCandidate(CheckRange Range, string Original, IReadOnlyList<string> CheckIds, IReadOnlyList<string> Evidence, string UnitIdentity);

public sealed record RewriteDecision(RewriteCandidate Candidate, string? Proposed, bool Accepted, string Reason, bool Advisory = false);

public sealed record RewriteOutcome(
    string Text,
    IReadOnlyList<RewriteDecision> Decisions,
    int Requested,
    int Accepted,
    int GeneratedTokens,
    GateRunResult Final,
    string SkipReason)
{
    public bool Changed => Accepted > 0;

    public IReadOnlyDictionary<string, int> RejectedByReason =>
        Decisions.Where(d => !d.Accepted && d.Proposed is not null).GroupBy(d => d.Reason, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

public sealed record RewriteAnswer(string? Text, int GeneratedTokens);

public sealed class RewritePass
{
    public const string ReasonOff = "rewrite pass off";

    public const string ReasonAwaitingUser = "awaiting the user";

    public const string ReasonUnchanged = "model returned the sentence unchanged";

    public const string ReasonNoAnswer = "model returned nothing";

    public const string ReasonPositional = "positional placeholders forbid reordering; finding degraded to advisory";

    public const string ReasonCueBoundary = "rewrite would cross a subtitle cue boundary";

    public const string ReasonPlaceholders = "a placeholder did not survive";

    public const string ReasonMeaning = "meaning not proved by the semantic checks";

    public const string ReasonMeaningUnavailable = "semantic services unavailable, meaning could not be proved";

    public const string ReasonTerminology = "a glossary term did not survive";

    public const string ReasonStructure = "a structure defect appeared";

    public const string ReasonCoverage = "a coverage defect appeared";

    public const string ReasonMargin = "naturalness did not improve by the configured margin";

    public const string ReasonCap = "rewrite cap reached";

    public const string ReasonAlreadyRewritten = "sentence is the pass's own accepted output";

    private readonly VerificationPipeline _pipeline;
    private readonly NaturalnessSettings _settings;
    private readonly Func<RewriteRequest, CancellationToken, Task<RewriteAnswer>> _ask;
    private readonly HashSet<string> _produced = new(StringComparer.Ordinal);

    public RewritePass(VerificationPipeline pipeline, NaturalnessSettings settings, Func<RewriteRequest, CancellationToken, Task<RewriteAnswer>> ask)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ask);

        _pipeline = pipeline;
        _settings = settings;
        _ask = ask;
    }

    public async Task<RewriteOutcome> RunAsync(
        string source,
        string target,
        IReadOnlyList<SegmentTrace> segments,
        string? sourceLanguage,
        string? targetLanguage,
        RepairAutonomy autonomy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(segments);

        var context = _pipeline.BuildContext(source, target, segments, sourceLanguage, targetLanguage);

        if (context is null)
        {
            return new RewriteOutcome(target, [], 0, 0, 0, GateRunResult.Empty("check context could not be built"), "check context could not be built");
        }

        var gate = _pipeline.RunGate(context);

        if (!_settings.RewriteEnabled || autonomy == RepairAutonomy.Off)
        {
            return new RewriteOutcome(target, [], 0, 0, 0, gate, ReasonOff);
        }

        var candidates = Candidates(context, gate);
        var decisions = new List<RewriteDecision>();
        var text = target;
        var traces = segments.ToList();
        var accepted = 0;
        var requested = 0;
        var tokens = 0;
        var offsetShift = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (accepted >= _settings.RewritesPerDocument)
            {
                decisions.Add(new RewriteDecision(candidate, null, false, ReasonCap));
                continue;
            }

            if (_produced.Contains(candidate.Original))
            {
                decisions.Add(new RewriteDecision(candidate, null, false, ReasonAlreadyRewritten));
                continue;
            }

            var range = new CheckRange(candidate.Range.UnitPath, candidate.Range.Offset + offsetShift, candidate.Range.Length);
            var sourceSentence = SourceSentenceFor(context, candidate);
            var permission = FormatLimits.Classify(sourceSentence, candidate.Original, context.Adapter.Format);

            if (permission == ReorderPermission.ForbiddenPositionalPlaceholders)
            {
                decisions.Add(new RewriteDecision(candidate, null, false, ReasonPositional, Advisory: true));
                continue;
            }

            if (permission == ReorderPermission.ForbiddenCueBoundary)
            {
                decisions.Add(new RewriteDecision(candidate, null, false, ReasonCueBoundary, Advisory: true));
                continue;
            }

            RewriteDecision? decision = null;

            for (var attempt = 0; attempt < Math.Max(1, _settings.CandidatesPerSentence) && decision is null; attempt++)
            {
                requested++;
                var request = Request(context, candidate, sourceSentence, text, range, targetLanguage ?? context.Settings.TargetLanguage);
                var answer = await _ask(request, cancellationToken).ConfigureAwait(false);
                tokens += answer.GeneratedTokens;
                var proposed = Clean(answer.Text);

                if (proposed is null)
                {
                    decision = new RewriteDecision(candidate, null, false, ReasonNoAnswer);
                    continue;
                }

                if (string.Equals(proposed, candidate.Original, StringComparison.Ordinal))
                {
                    decision = new RewriteDecision(candidate, proposed, false, ReasonUnchanged);
                    continue;
                }

                if (!FormatLimits.PlaceholdersSurvive(candidate.Original, proposed))
                {
                    decision = new RewriteDecision(candidate, proposed, false, ReasonPlaceholders);
                    continue;
                }

                if (autonomy == RepairAutonomy.AskEveryTime)
                {
                    decision = new RewriteDecision(candidate, proposed, false, ReasonAwaitingUser);
                    continue;
                }

                var spliced = Splice(text, range, proposed);
                var shifted = ShiftTraces(traces, range, proposed.Length - range.Length);
                var verdict = Accept(source, spliced, shifted, sourceLanguage, targetLanguage, context, gate, candidate, range, proposed, autonomy);

                if (verdict is null)
                {
                    text = spliced;
                    traces = shifted;
                    offsetShift += proposed.Length - range.Length;
                    accepted++;
                    _produced.Add(proposed);
                    decision = new RewriteDecision(candidate, proposed, true, string.Empty);
                }
                else
                {
                    decision = new RewriteDecision(candidate, proposed, false, verdict);
                }
            }

            decisions.Add(decision ?? new RewriteDecision(candidate, null, false, ReasonNoAnswer));
        }

        var final = accepted > 0 ? _pipeline.RunGate(source, text, traces, sourceLanguage, targetLanguage) : gate;

        return new RewriteOutcome(text, decisions, requested, accepted, tokens, final, string.Empty);
    }

    public static IReadOnlyList<RewriteCandidate> Candidates(CheckContext context, GateRunResult gate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);

        var sentences = NaturalnessCheck.Sentences(context);
        var candidates = new List<RewriteCandidate>();

        foreach (var sentence in sentences)
        {
            var findings = gate.Routed
                .Where(r => r.Action == CheckAction.Rewrite && r.Severity == CheckSeverity.Score && r.TargetRange.Overlaps(sentence.Range))
                .ToList();

            if (findings.Count == 0 || context.Exemptions.Spans.Any(s => s.Range.Overlaps(sentence.Range)))
            {
                continue;
            }

            candidates.Add(new RewriteCandidate(
                sentence.Range,
                context.Target.Text.Substring(sentence.Range.Offset, sentence.Range.Length),
                [.. findings.SelectMany(f => f.CheckIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. findings.Select(f => f.Finding.Evidence).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                sentence.Unit.Identity));
        }

        return [.. candidates.OrderBy(c => c.Range.Offset)];
    }

    private string? Accept(
        string source,
        string spliced,
        IReadOnlyList<SegmentTrace> traces,
        string? sourceLanguage,
        string? targetLanguage,
        CheckContext before,
        GateRunResult gateBefore,
        RewriteCandidate candidate,
        CheckRange range,
        string proposed,
        RepairAutonomy autonomy)
    {
        var after = _pipeline.BuildContext(source, spliced, traces, sourceLanguage, targetLanguage);

        if (after is null)
        {
            return ReasonStructure;
        }

        var newRange = new CheckRange(range.UnitPath, range.Offset, proposed.Length);
        var probe = new CheckFinding(CheckId.Coverage.SourceTokenSurvival, newRange, SourceRangeFor(before, candidate), CheckGranularity.Sentence, CheckSeverity.Score, 100, CheckCause.ModelOutput, "rewrite candidate", CheckAction.Mark);
        var gateAfter = _pipeline.RunGate(after, [probe]);
        var semantics = SemanticPorts.For(after);

        if (!TerminologySurvives(before, after, candidate.Original, proposed) || gateAfter.Routed.Any(r => r.Category == CheckId.Terminology.Category && r.Severity == CheckSeverity.Defect && r.TargetRange.Overlaps(newRange)))
        {
            return ReasonTerminology;
        }

        if (!semantics.Embeddings.Available)
        {
            return ReasonMeaningUnavailable;
        }

        var sourceSentence = SourceSentenceFor(before, candidate);
        var sourceVector = semantics.Embeddings.Embed(sourceSentence);
        var originalVector = semantics.Embeddings.Embed(candidate.Original);
        var proposedVector = semantics.Embeddings.Embed(proposed);

        if (sourceVector is null || originalVector is null || proposedVector is null)
        {
            return ReasonMeaningUnavailable;
        }

        var tolerance = NaturalnessPorts.For(before).Profile.MeaningTolerance;

        if (EmbeddingMath.Cosine(sourceVector, proposedVector) < EmbeddingMath.Cosine(sourceVector, originalVector) - tolerance)
        {
            return ReasonMeaning;
        }

        if (Count(gateAfter, CheckId.Structure.Category, CheckSeverity.Defect) > Count(gateBefore, CheckId.Structure.Category, CheckSeverity.Defect))
        {
            return ReasonStructure;
        }

        if (Count(gateAfter, CheckId.Coverage.Category, CheckSeverity.Defect) > Count(gateBefore, CheckId.Coverage.Category, CheckSeverity.Defect))
        {
            return ReasonCoverage;
        }

        var naturalnessBefore = gateBefore.Routed.Count(r => r.Category == CheckId.Naturalness.Category && r.TargetRange.Overlaps(range));
        var naturalnessAfter = gateAfter.Routed.Count(r => r.Category == CheckId.Naturalness.Category && r.TargetRange.Overlaps(newRange));

        if (naturalnessBefore - naturalnessAfter < Math.Max(1, _settings.ImprovementMargin) && autonomy != RepairAutonomy.RepairEverything)
        {
            return ReasonMargin;
        }

        var fit = RewriteFit(after, newRange);

        if (fit is { } expected)
        {
            NaturalnessEvidenceStore.For(after).ExpectSentences(candidate.UnitIdentity, expected);
        }

        return null;
    }

    private static int? RewriteFit(CheckContext after, CheckRange range)
    {
        var pair = CoverageAlignment.Of(after).Pairs.FirstOrDefault(p => p.TargetRange is not null && p.TargetRange.Overlaps(range));

        return pair is null ? null : Core.Verification.Checks.Ratio.RatioMeasures.SentenceCount(pair.TargetRaw);
    }

    private static bool TerminologySurvives(CheckContext before, CheckContext after, string original, string proposed)
    {
        var services = TerminologyPorts.For(before);
        var lemmatizer = services.Lemmatizer;

        foreach (var entry in services.Glossary.Entries.Where(e => !e.DoNotTranslate))
        {
            var accepted = Lemmas.Sequence(lemmatizer, entry.Accepted);
            var originalLemmas = Lemmas.Sequence(lemmatizer, original);
            var proposedLemmas = Lemmas.Sequence(lemmatizer, proposed);

            if (Contains(originalLemmas, accepted) && !Contains(proposedLemmas, accepted))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0)
        {
            return true;
        }

        for (var i = 0; i + needle.Count <= haystack.Count; i++)
        {
            var match = true;

            for (var j = 0; j < needle.Count; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static int Count(GateRunResult gate, string category, CheckSeverity severity) =>
        gate.Routed.Count(r => r.Category == category && r.Severity == severity);

    private static RewriteRequest Request(CheckContext context, RewriteCandidate candidate, string sourceSentence, string text, CheckRange range, string targetLanguage)
    {
        var previous = Neighbour(text, range, before: true);
        var next = Neighbour(text, range, before: false);
        var glossary = TerminologyPorts.For(context).Glossary.Entries
            .Where(e => !e.DoNotTranslate && sourceSentence.Contains(e.Source, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Source + " = " + e.Accepted)
            .ToList();
        var services = NaturalnessPorts.For(context);
        var pack = services.PackFor(context.Settings.TargetLanguage);
        var register = pack is null || !services.Tagger.Available ? string.Empty : Describe(RegisterProfiles.Document(context, pack, services.Tagger));

        return new RewriteRequest(sourceSentence, candidate.Original, previous, next, glossary, register, candidate.Evidence, targetLanguage);
    }

    private static string Describe(RegisterProfile profile) =>
        string.Join(", ", new[] { profile.Formality, profile.Person, profile.Instruction }.Where(v => v != RegisterProfile.Unknown));

    private static string Neighbour(string text, CheckRange range, bool before)
    {
        if (before)
        {
            var start = range.Offset;
            var cut = text.LastIndexOfAny(['.', '!', '?', '\n'], Math.Max(0, start - 2));
            var from = cut < 0 ? 0 : cut + 1;
            return text[from..start].Trim();
        }

        var end = range.End;
        var stop = text.IndexOfAny(['.', '!', '?', '\n'], Math.Min(text.Length, end));
        var to = stop < 0 ? text.Length : stop + 1;
        return text[Math.Min(end, text.Length)..to].Trim();
    }

    private static string SourceSentenceFor(CheckContext context, RewriteCandidate candidate)
    {
        var pair = CoverageAlignment.Of(context).Pairs.FirstOrDefault(p => string.Equals(p.Identity, candidate.UnitIdentity, StringComparison.Ordinal));

        return pair is null ? string.Empty : pair.SourceRaw.Trim();
    }

    private static CheckRange? SourceRangeFor(CheckContext context, RewriteCandidate candidate) =>
        CoverageAlignment.Of(context).Pairs.FirstOrDefault(p => string.Equals(p.Identity, candidate.UnitIdentity, StringComparison.Ordinal))?.SourceRange;

    public static string Splice(string text, CheckRange range, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(replacement);

        return string.Concat(text.AsSpan(0, range.Offset), replacement, text.AsSpan(range.End));
    }

    public static List<SegmentTrace> ShiftTraces(IReadOnlyList<SegmentTrace> traces, CheckRange range, int delta)
    {
        ArgumentNullException.ThrowIfNull(traces);
        ArgumentNullException.ThrowIfNull(range);

        var shifted = new List<SegmentTrace>();

        foreach (var trace in traces)
        {
            if (trace.TargetStart is not { } start)
            {
                shifted.Add(trace);
                continue;
            }

            var length = trace.TargetLength ?? 0;

            if (start + length <= range.Offset)
            {
                shifted.Add(trace);
            }
            else if (start >= range.End)
            {
                shifted.Add(trace with { TargetStart = start + delta });
            }
            else
            {
                shifted.Add(trace with { TargetLength = length + delta, Answer = null, Spliced = null });
            }
        }

        return shifted;
    }

    private static string? Clean(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var line = answer.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        return string.IsNullOrWhiteSpace(line) ? null : line.Trim('"', '“', '”').Trim();
    }

    public string Summary(RewriteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"requested {outcome.Requested}, accepted {outcome.Accepted}, rejected {outcome.Decisions.Count(d => !d.Accepted)}, tokens {outcome.GeneratedTokens}");
    }
}
