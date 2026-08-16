using System.Text.RegularExpressions;

namespace BetterTranslator.Runtime.Verification;

public sealed record SamplerSettings(double Temperature, int TopK, double TopP, double MinP, double RepeatPenalty);

public sealed record ConfigAdvisory(string RuleId, string FindingIds, string Message);

public static partial class SamplerConfigGuard
{
    [GeneratedRegex(@"\b(i?q[1-4](_[a-z0-9_]+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowQuantPattern { get; }

    public static SamplerSettings Recommended() =>
        new(Temperature: 0, TopK: 1, TopP: 1, MinP: 0, RepeatPenalty: 1.0);

    public static bool AppliesTo(string? modelId) =>
        modelId is not null && modelId.Contains("translategemma", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ConfigAdvisory> Validate(string modelId, SamplerSettings s, bool kvCacheQuantized)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(s);

        var list = new List<ConfigAdvisory>();

        if (AppliesTo(modelId))
        {
            if (s.Temperature != 0)
            {
                list.Add(new ConfigAdvisory(
                    "STAGE0-TEMP",
                    "F9,F10,F36",
                    $"temperature={s.Temperature}; model card's authored example is greedy (do_sample=False). Set temperature=0."));
            }

            if (s.TopK != 1)
            {
                list.Add(new ConfigAdvisory(
                    "STAGE0-TOPK",
                    "F9,F10",
                    $"top_k={s.TopK}; set top_k=1 to match greedy decoding."));
            }

            if (Math.Abs(s.RepeatPenalty - 1.0) > double.Epsilon)
            {
                list.Add(new ConfigAdvisory(
                    "STAGE0-REPPEN",
                    "F16,F17",
                    $"repeat_penalty={s.RepeatPenalty}; penalties push the decoder off correct morphemes onto malformed continuations. Set 1.0 (disabled)."));
            }

            if (s.MinP != 0)
            {
                list.Add(new ConfigAdvisory(
                    "STAGE0-MINP",
                    "F9",
                    $"min_p={s.MinP}; set 0 under greedy decoding."));
            }
        }

        var lowQuant = LowQuantPattern.IsMatch(modelId);
        var qat = modelId.Contains("qat", StringComparison.OrdinalIgnoreCase);

        if (lowQuant && !qat)
        {
            list.Add(new ConfigAdvisory(
                "STAGE0-QUANT",
                "F13,F15,F11",
                $"model '{modelId}' looks quantized at Q4 or below; run Q6_K or higher, or a Google QAT Q4_0 checkpoint."));
        }

        if (kvCacheQuantized)
        {
            list.Add(new ConfigAdvisory(
                "STAGE0-KV",
                "F14",
                "quantized KV cache is not lossless for Gemma (q8_0 KV KL 0.108); disable KV cache quantization."));
        }

        return list;
    }
}

public interface ITokenLogprobSource
{
    bool TryGetLogprobs(string requestId, out IReadOnlyList<(string Token, double LogProb)> logprobs, out string reason);
}

public sealed class UnavailableTokenLogprobSource : ITokenLogprobSource
{
    public bool TryGetLogprobs(string requestId, out IReadOnlyList<(string, double)> logprobs, out string reason)
    {
        logprobs = [];
        reason = "before-sampler logprobs not exposed by the LM Studio endpoint; requires llama.cpp direct (open question O1)";
        return false;
    }
}
