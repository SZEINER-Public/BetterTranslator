using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Verification;

public sealed class SessionReverseTranslator : IReverseTranslator
{
    private readonly IInferenceSession _session;

    private readonly TranslationJob _template;

    private int _calls;

    private int _tokens;

    private TimeSpan _elapsed;

    public SessionReverseTranslator(IInferenceSession session, TranslationJob template)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(template);

        _session = session;
        _template = template;
    }

    public string ModelIdentity => Path.GetFileName(_template.ModelPath);

    public bool Available => _session.IsAlive && _template.Direction.CanSwap;

    public string UnavailableReason =>
        !_session.IsAlive ? "the inference session is not alive"
        : !_template.Direction.CanSwap ? "the translation direction cannot be swapped"
        : string.Empty;

    public int Calls => _calls;

    public int GeneratedTokens => _tokens;

    public TimeSpan Elapsed => _elapsed;

    public string? Translate(string text, string fromLanguage, string toLanguage)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!Available)
        {
            return null;
        }

        var reversed = _template with { Text = text, Direction = _template.Direction.Swapped() };
        var prompt = _session.BuildTranslatePrompt(text, reversed.From, reversed.To);
        var sampling = reversed.Sampling(0);

        _calls++;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var completion = _session.CompleteCounted(prompt, sampling, CancellationToken.None);
        _elapsed += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        _tokens += Math.Max(0, completion.Tokens);

        return completion.Faulted || string.IsNullOrWhiteSpace(completion.Text) ? null : completion.Text.Trim();
    }
}

public static class SemanticRuntime
{
    public const string ModelSource = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/onnx/model.onnx";

    public const string TokenizerSource = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/tokenizer.json";

    public const long ModelBytes = 470268510;

    public const string ModelSha256 = "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665";

    public const long TokenizerBytes = 17082730;

    public const string TokenizerSha256 = "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39";

    public const string NoSessionReason = "no inference session is loaded for reverse translation";

    public static ModelComponent EmbeddingComponent(EmbeddingModelDescription? model = null)
    {
        model ??= EmbeddingModelDescription.MultilingualE5Small;

        return new ModelComponent
        {
            Id = "embedding-" + model.FileName.Replace(".onnx", string.Empty, StringComparison.OrdinalIgnoreCase),
            Name = model.Identity,
            Kind = ComponentKind.Verification,
            Summary = $"Sentence embedding model for the semantic checks ({model.License}). Optional; the checks stay skipped without it.",
            SizeBytes = ModelBytes,
            Version = "onnx-fp32",
            ArtifactFileName = model.FileName,
            DownloadUrl = new Uri(ModelSource),
            Sha256 = ModelSha256,
            IsRequired = false,
            Companions =
            [
                new CompanionArtifact
                {
                    FileName = EmbeddingModelStore.TokenizerFileName,
                    SizeBytes = TokenizerBytes,
                    Sha256 = TokenizerSha256,
                    Reason = "Tokenizer the embedding model reads text through.",
                    DownloadUrl = new Uri(TokenizerSource),
                },
            ],
        };
    }

    public static SemanticSettings SettingsFor(VerificationSettings verification)
    {
        ArgumentNullException.ThrowIfNull(verification);

        return SemanticSettings.Default with
        {
            ReverseCap = Math.Max(0, verification.SemanticReverseCap),
            WholeUnitShare = FinalRepairPass.WholeLineShare,
        };
    }

    public static SemanticServices Services(
        InstallPaths installPaths,
        IInferenceSession? session,
        TranslationJob? template,
        SemanticSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(installPaths);

        var embeddings = EmbeddingModelStore.Host(installPaths.ModelsFolder);

        IReverseTranslator reverse = session is not null && template is not null
            ? new SessionReverseTranslator(session, template)
            : new UnavailableReverseTranslator(NoSessionReason);

        return new SemanticServices(
            embeddings,
            reverse,
            settings ?? SemanticSettings.Default with { WholeUnitShare = FinalRepairPass.WholeLineShare });
    }
}
