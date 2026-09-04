namespace BetterTranslator.Core.Verification.Checks;

public enum ExemptionReason
{
    MaskPlaceholder,
    GlossaryDoNotTranslate,
    ProtectedName,
    CodeSpan,
    Url,
    FormatPlaceholder,
    SettingsRule,
    IdenticalRendering,
}

public sealed record ExemptSpan(CheckRange Range, ExemptionReason Reason, string Text, bool Inflectable = false);

public interface IExemptionRegistry
{
    IReadOnlyList<ExemptSpan> Spans { get; }

    ExemptSpan? Covering(CheckRange range);

    bool IsExempt(CheckRange range);
}

public sealed class ExemptionRegistry : IExemptionRegistry
{
    private readonly List<ExemptSpan> _spans = [];

    public static ExemptionRegistry Empty { get; } = new();

    public IReadOnlyList<ExemptSpan> Spans => _spans;

    public ExemptionRegistry Add(ExemptSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);

        _spans.Add(span);
        return this;
    }

    public ExemptSpan? Covering(CheckRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        return _spans.FirstOrDefault(span => span.Range.Contains(range));
    }

    public bool IsExempt(CheckRange range) => Covering(range) is not null;
}
