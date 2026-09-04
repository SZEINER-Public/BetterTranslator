using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Semantics;
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
        string? targetLanguage)
    {
        var runSettings = new CheckRunSettings
        {
            SourceLanguage = Code(sourceLanguage),
            TargetLanguage = Code(targetLanguage),
        };

        CheckContext context;

        try
        {
            context = StructureContext.Build(source, target, null, segments, settings: runSettings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return GateRunResult.Empty("check context could not be built: " + ex.GetType().Name);
        }

        if (Semantics?.Invoke(context) is { } services)
        {
            SemanticPorts.Attach(context, services);
        }

        var gate = new VerificationGate(_registry, _settings.Gate)
        {
            BeforeEscalation = (ctx, findings) => EscalationGate.Admit(ctx, findings, SemanticPorts.For(ctx).Settings.SpanCap),
        };

        return gate.Run(context);
    }

    private static string Code(string? language) =>
        string.IsNullOrWhiteSpace(language) ? string.Empty : language.Split('-', '_')[0].ToLowerInvariant();
}
