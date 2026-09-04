using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BetterTranslator.Core.Verification.Checks.Semantics;

public sealed record EmbeddingModelDescription(string Identity, string FileName, string SizeOnDisk, string License, IReadOnlyList<string> Languages)
{
    public static EmbeddingModelDescription MultilingualE5Small { get; } = new(
        "intfloat/multilingual-e5-small",
        "multilingual-e5-small.onnx",
        "471 MB fp32, 118 MB int8",
        "MIT",
        ["en", "cs", "de", "fr", "es", "it", "pl", "pt", "nl", "ru", "uk", "sk", "hu", "ro", "bg", "el", "tr", "ar", "zh-CN", "zh-TW", "ja", "ko", "hi", "he", "fa", "ur", "sv", "da", "fi", "nb"]);

    public string QueryPrefix => "query: ";
}

public interface IEmbeddingBackend : IDisposable
{
    string ModelIdentity { get; }

    float[,] HiddenStates(IReadOnlyList<int> ids);
}

public interface IEmbeddingHost
{
    string ModelIdentity { get; }

    bool Available { get; }

    string UnavailableReason { get; }

    float[]? Embed(string text);

    int Calls { get; }
}

public static class EmbeddingMath
{
    public static float[] MeanPool(float[,] hidden, IReadOnlyList<int> mask)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(mask);

        var tokens = hidden.GetLength(0);
        var dimension = hidden.GetLength(1);
        var pooled = new float[dimension];
        var kept = 0;

        for (var t = 0; t < tokens; t++)
        {
            if (t < mask.Count && mask[t] == 0)
            {
                continue;
            }

            kept++;

            for (var d = 0; d < dimension; d++)
            {
                pooled[d] += hidden[t, d];
            }
        }

        if (kept > 0)
        {
            for (var d = 0; d < dimension; d++)
            {
                pooled[d] /= kept;
            }
        }

        return pooled;
    }

    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));

        if (norm == 0)
        {
            return vector;
        }

        return [.. vector.Select(v => (float)(v / norm))];
    }

    public static double Cosine(float[] a, float[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0;
        double na = 0;
        double nb = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : Math.Clamp(dot / Math.Sqrt(na * nb), -1, 1);
    }

    public static int SimilarityConfidence(double cosine) =>
        (int)Math.Clamp(Math.Round(100 * (1 - Math.Max(0, cosine)), MidpointRounding.AwayFromZero), 0, 100);
}

public sealed class EmbeddingHost : IEmbeddingHost, IDisposable
{
    private readonly Lazy<(IEmbeddingBackend? Backend, UnigramTokenizer? Tokenizer, string Reason)> _warm;

    private readonly string _queryPrefix;

    private int _calls;

    public EmbeddingHost(Func<IEmbeddingBackend> backendFactory, Func<UnigramTokenizer> tokenizerFactory, string modelIdentity, string queryPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(tokenizerFactory);

        ModelIdentity = modelIdentity;
        _queryPrefix = queryPrefix;
        _warm = new Lazy<(IEmbeddingBackend?, UnigramTokenizer?, string)>(() => Warm(backendFactory, tokenizerFactory));
    }

    public static EmbeddingHost Unavailable(string modelIdentity, string reason) =>
        new(() => throw new InvalidOperationException(reason), () => throw new InvalidOperationException(reason), modelIdentity);

    public string ModelIdentity { get; }

    public bool Available => _warm.Value.Backend is not null && _warm.Value.Tokenizer is not null;

    public string UnavailableReason => _warm.Value.Reason;

    public int Calls => _calls;

    public float[]? Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var (backend, tokenizer, _) = _warm.Value;

        if (backend is null || tokenizer is null)
        {
            return null;
        }

        _calls++;
        var encoded = tokenizer.Encode(_queryPrefix + text);
        var hidden = backend.HiddenStates(encoded.Ids);
        var mask = Enumerable.Repeat(1, encoded.Ids.Count).ToList();

        return EmbeddingMath.Normalize(EmbeddingMath.MeanPool(hidden, mask));
    }

    public void Dispose()
    {
        if (_warm.IsValueCreated)
        {
            _warm.Value.Backend?.Dispose();
        }
    }

    private static (IEmbeddingBackend?, UnigramTokenizer?, string) Warm(Func<IEmbeddingBackend> backendFactory, Func<UnigramTokenizer> tokenizerFactory)
    {
        try
        {
            var tokenizer = tokenizerFactory();
            var backend = backendFactory();

            return (backend, tokenizer, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or OnnxRuntimeException or DllNotFoundException)
        {
            return (null, null, ex.Message);
        }
    }
}

public sealed class OnnxEmbeddingBackend : IEmbeddingBackend
{
    private readonly InferenceSession _session;

    private readonly string _inputIds;

    private readonly string? _attentionMask;

    private readonly string? _tokenTypeIds;

    private readonly string _output;

    public OnnxEmbeddingBackend(string modelPath, string modelIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("embedding model file is not installed", modelPath);
        }

        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        _session = new InferenceSession(modelPath, options);
        ModelIdentity = modelIdentity;

        var inputs = _session.InputMetadata.Keys.ToList();
        _inputIds = inputs.FirstOrDefault(k => k.Contains("input_ids", StringComparison.Ordinal)) ?? inputs[0];
        _attentionMask = inputs.FirstOrDefault(k => k.Contains("attention_mask", StringComparison.Ordinal));
        _tokenTypeIds = inputs.FirstOrDefault(k => k.Contains("token_type", StringComparison.Ordinal));
        _output = _session.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("last_hidden_state", StringComparison.Ordinal)) ?? _session.OutputMetadata.Keys.First();
    }

    public string ModelIdentity { get; }

    public float[,] HiddenStates(IReadOnlyList<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var length = ids.Count;
        var idTensor = new DenseTensor<long>([1, length]);
        var maskTensor = new DenseTensor<long>([1, length]);
        var typeTensor = new DenseTensor<long>([1, length]);

        for (var i = 0; i < length; i++)
        {
            idTensor[0, i] = ids[i];
            maskTensor[0, i] = 1;
            typeTensor[0, i] = 0;
        }

        var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputIds, idTensor) };

        if (_attentionMask is not null)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor(_attentionMask, maskTensor));
        }

        if (_tokenTypeIds is not null)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIds, typeTensor));
        }

        using var results = _session.Run(feeds, [_output]);
        var tensor = results.First().AsTensor<float>();
        var dimension = tensor.Dimensions[^1];
        var hidden = new float[length, dimension];

        for (var t = 0; t < length; t++)
        {
            for (var d = 0; d < dimension; d++)
            {
                hidden[t, d] = tensor[0, t, d];
            }
        }

        return hidden;
    }

    public void Dispose() => _session.Dispose();
}
