using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Naturalness;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Core.Verification.Checks.Terminology;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Verification.Structure;

namespace BetterTranslator.Engine.Verification;

public sealed class VerificationPipeline
{
    public const string NoDictionaryReason = "no dictionary configured; checks ran without word scoring";

    private readonly TranslationVerifier? _verifier;
    private readonly VerificationSettings _settings;
    private readonly CheckRegistry _registry;

    public VerificationPipeline(TranslationVerifier? verifier, VerificationSettings settings, CheckRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _verifier = verifier;
        _settings = settings;
        _registry = registry ?? CheckRegistry.Default;
    }

    public Func<CheckContext, SemanticServices?>? Semantics { get; init; }

    public Func<CheckContext, TerminologyServices?>? Terminology { get; init; }

    public Func<CheckContext, NaturalnessServices?>? Naturalness { get; init; }

    public VerificationSettings Settings => _settings;

    public bool HasVerifier => _verifier is not null;

    public VerificationResult Verify(
        string source,
        string target,
        IReadOnlyList<SegmentTrace> segments,
        string? sourceLanguage = null,
        string? targetLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(segments);

        var gate = RunGate(source, target, segments, sourceLanguage, targetLanguage);
        var result = _verifier?.Verify(source, target, gate) ?? VerificationResult.Skipped(NoDictionaryReason);
        result.Gate = gate;

        return result;
    }

    public GateRunResult RunGate(
        string source,
        string target,
        IReadOnlyList<SegmentTrace> segments,
        string? sourceLanguage,
        string? targetLanguage,
        IReadOnlyList<CheckFinding>? escalate = null,
        Action<CheckContext>? attach = null)
    {
        var context = BuildContext(source, target, segments, sourceLanguage, targetLanguage);

        if (context is null)
        {
            return GateRunResult.Empty("check context could not be built");
        }

        attach?.Invoke(context);

        return RunGate(context, escalate);
    }

    public GateRunResult RunGate(CheckContext context, IReadOnlyList<CheckFinding>? escalate = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Semantics?.Invoke(context) is { } semantics)
        {
            SemanticPorts.Attach(context, semantics);
        }

        if (Terminology?.Invoke(context) is { } terminology)
        {
            TerminologyPorts.Attach(context, terminology);
        }

        if (Naturalness?.Invoke(context) is { } naturalness)
        {
            NaturalnessPorts.Attach(context, naturalness);
        }

        var gate = new VerificationGate(_registry, _settings.Gate)
        {
            BeforeEscalation = (ctx, findings) => EscalationGate.Admit(ctx, [.. findings, .. escalate ?? []], SemanticPorts.For(ctx).Settings.SpanCap),
        };

        return gate.Run(context) with { Escalation = EscalationSummary.For(context) };
    }

    public CheckContext? BuildContext(
        string source,
        string target,
        IReadOnlyList<SegmentTrace> segments,
        string? sourceLanguage,
        string? targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(segments);

        var runSettings = new CheckRunSettings
        {
            SourceLanguage = Code(sourceLanguage),
            TargetLanguage = Code(targetLanguage),
        };

        try
        {
            return StructureContext.Build(source, target, null, segments, settings: runSettings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static string Code(string? language) =>
        string.IsNullOrWhiteSpace(language) ? string.Empty : language.Split('-', '_')[0].ToLowerInvariant();
}
