using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Coverage;
using BetterTranslator.Engine.Verification.Structure;

namespace BetterTranslator.Engine.Verification.Coverage;

public static class CompletionReporting
{
    public static CompletionReport? For(
        string source,
        string? target,
        IReadOnlyList<SegmentTrace> segments,
        IFormatAdapter? adapter = null,
        CheckRunSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(segments);

        if (target is null)
        {
            return null;
        }

        var context = StructureContext.Build(source, target, adapter, segments, settings: settings);

        return CompletionMetric.Compute(context);
    }
}
