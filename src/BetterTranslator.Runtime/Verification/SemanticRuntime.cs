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
        var completion = _session.CompleteCounted(prompt, sampling, CancellationToken.None);

        return completion.Faulted || string.IsNullOrWhiteSpace(completion.Text) ? null : completion.Text.Trim();
    }
}

public static class SemanticRuntime
{
    public static ModelComponent EmbeddingComponent(EmbeddingModelDescription? model = null)
    {
        model ??= EmbeddingModelDescription.MultilingualE5Small;

        return new ModelComponent
        {
            Id = "embedding-" + model.FileName.Replace(".onnx", string.Empty, StringComparison.OrdinalIgnoreCase),
            Name = model.Identity,
            Kind = ComponentKind.Model,
            Summary = $"Sentence embedding model for semantic verification ({model.SizeOnDisk}, {model.License}).",
            SizeBytes = 471_000_000,
            Version = "onnx-fp32",
            ArtifactFileName = model.FileName,
            IsRequired = false,
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
            : new UnavailableReverseTranslator("no inference session is loaded for reverse translation");

        return new SemanticServices(
            embeddings,
            reverse,
            settings ?? SemanticSettings.Default with { WholeUnitShare = FinalRepairPass.WholeLineShare });
    }
}
