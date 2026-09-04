namespace BetterTranslator.Core.Verification.Checks.Terminology;

public sealed record TerminologyEntry(string Source, string Accepted, IReadOnlyList<string> Rejected)
{
    public bool DoNotTranslate => Accepted.Length == 0;
}

public sealed class TerminologyGlossary
{
    private readonly SortedDictionary<string, TerminologyEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static TerminologyGlossary Empty { get; } = new();

    public IReadOnlyList<TerminologyEntry> Entries => [.. _entries.Values];

    public bool IsEmpty => _entries.Count == 0;

    public TerminologyGlossary Add(TerminologyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Source.Trim().Length == 0)
        {
            return this;
        }

        if (_entries.TryGetValue(entry.Source, out var existing))
        {
            var accepted = entry.Accepted.Length > 0 ? entry.Accepted : existing.Accepted;
            var rejected = existing.Rejected.Concat(entry.Rejected).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _entries[entry.Source] = new TerminologyEntry(existing.Source, accepted, rejected);
            return this;
        }

        _entries[entry.Source] = entry;
        return this;
    }

    public TerminologyGlossary Add(string source, string accepted, params string[] rejected) =>
        Add(new TerminologyEntry(source, accepted, rejected));

    public TerminologyGlossary Replace(TerminologyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Source.Trim().Length > 0)
        {
            _entries[entry.Source] = entry;
        }

        return this;
    }

    public TerminologyEntry? Find(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return _entries.TryGetValue(source, out var entry) ? entry : null;
    }
}
