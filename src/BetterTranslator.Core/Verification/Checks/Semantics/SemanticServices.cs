using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace BetterTranslator.Core.Verification.Checks.Semantics;

public interface IReverseTranslator
{
    string ModelIdentity { get; }

    bool Available { get; }

    string UnavailableReason { get; }

    string? Translate(string text, string fromLanguage, string toLanguage);

    int Calls { get; }

    int GeneratedTokens { get; }

    TimeSpan Elapsed { get; }
}

public sealed class UnavailableReverseTranslator(string reason) : IReverseTranslator
{
    public string ModelIdentity => "none";

    public bool Available => false;

    public string UnavailableReason { get; } = reason;

    public int Calls => 0;

    public int GeneratedTokens => 0;

    public TimeSpan Elapsed => TimeSpan.Zero;

    public string? Translate(string text, string fromLanguage, string toLanguage) => null;
}

public sealed record SemanticSettings(int SpanCap, double WholeUnitShare, int ReverseCap = 24)
{
    public static SemanticSettings Default { get; } = new(SpanCap: 24, WholeUnitShare: 0.6, ReverseCap: 24);
}

public sealed class CappedReverseTranslator(IReverseTranslator inner, int cap) : IReverseTranslator
{
    public const string DisabledReason = "reverse translation disabled: the reverse translation cap is 0";

    public int Cap { get; } = Math.Max(0, cap);

    public IReverseTranslator Inner { get; } = inner;

    public string ModelIdentity => Inner.ModelIdentity;

    public bool Available => Cap > 0 && Inner.Calls < Cap && Inner.Available;

    public string UnavailableReason =>
        Cap == 0 ? DisabledReason
        : !Inner.Available ? Inner.UnavailableReason
        : Inner.Calls >= Cap ? $"reverse translation cap of {Cap} reached"
        : string.Empty;

    public int Calls => Inner.Calls;

    public int GeneratedTokens => Inner.GeneratedTokens;

    public TimeSpan Elapsed => Inner.Elapsed;

    public string? Translate(string text, string fromLanguage, string toLanguage) =>
        Available ? Inner.Translate(text, fromLanguage, toLanguage) : null;
}

public sealed record SemanticResult(double? Similarity, string? Reverse, double? ReverseSimilarity, double? TokenOverlap);

public sealed class SemanticCache
{
    private readonly Dictionary<string, SemanticResult> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public int Hits { get; private set; }

    public static string Key(string source, string target, string modelIdentity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(modelIdentity);

        var bytes = Encoding.UTF8.GetBytes(source + "" + target + "" + modelIdentity);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public SemanticResult Get(string key) => _entries.TryGetValue(key, out var result) ? result : new SemanticResult(null, null, null, null);

    public bool TryGet(string key, out SemanticResult result)
    {
        if (_entries.TryGetValue(key, out result!))
        {
            Hits++;
            return true;
        }

        result = new SemanticResult(null, null, null, null);
        return false;
    }

    public void Set(string key, SemanticResult result) => _entries[key] = result;
}

public sealed class SemanticServices
{
    public SemanticServices(IEmbeddingHost embeddings, IReverseTranslator reverse, SemanticSettings? settings = null, SemanticCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(reverse);

        Embeddings = embeddings;
        Settings = settings ?? SemanticSettings.Default;
        Reverse = new CappedReverseTranslator(reverse, Settings.ReverseCap);
        Cache = cache ?? new SemanticCache();
    }

    public IEmbeddingHost Embeddings { get; }

    public IReverseTranslator Reverse { get; }

    public SemanticSettings Settings { get; }

    public SemanticCache Cache { get; }

    public bool ReverseEnabled => Settings.ReverseCap > 0;

    public static SemanticServices Unavailable(string reason) =>
        new(EmbeddingHost.Unavailable("none", reason), new UnavailableReverseTranslator(reason));
}

public static class SemanticPorts
{
    private static readonly ConditionalWeakTable<CheckContext, SemanticServices> Attached = new();

    public const string NotAttachedReason = "no semantic services were attached to this run";

    public static void Attach(CheckContext context, SemanticServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        Attached.AddOrUpdate(context, services);
    }

    public static SemanticServices For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Attached.TryGetValue(context, out var services) ? services : SemanticServices.Unavailable(NotAttachedReason);
    }
}
