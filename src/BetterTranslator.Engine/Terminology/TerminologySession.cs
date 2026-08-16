using System.Collections.Concurrent;

namespace BetterTranslator.Engine.Terminology;

/// <summary>
/// One rendering per term, for the length of one document.
///
/// The per-unit corrector cannot see this: it is handed one line at a time and
/// two lines can each be individually correct while disagreeing with each other.
/// Measured on the reference engine's own corpus, `provision` came back eight
/// different ways in a single document, every one of them a real Czech word.
/// A reader does not read a document one line at a time.
///
/// The table wins where it speaks. Where it is silent the first accepted
/// rendering wins, which is arbitrary but consistent, and consistency is the
/// whole point.
/// </summary>
public sealed class TerminologySession(DomainTermTable table)
{
    private readonly ConcurrentDictionary<string, string> _chosen = new(StringComparer.OrdinalIgnoreCase);

    public DomainTermTable Table { get; } = table;

    /// <summary>
    /// Corrects one unit and records what it settled on, so a later unit that
    /// renders the same term differently is pulled back into line.
    /// </summary>
    public TerminologyResult Apply(string source, string translated)
    {
        var corrected = TerminologyCorrector.Apply(source, translated, Table);
        var corrections = corrected.Corrections.ToList();
        var text = corrected.Text;

        foreach (var term in Table.Present(source))
        {
            if (term.KeepInSource)
            {
                continue;
            }

            var settled = _chosen.GetOrAdd(term.Source, term.Accepted);

            if (string.Equals(settled, term.Accepted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var converged = TerminologyCorrector.Converge(text, term.Accepted, settled);

            if (converged is null)
            {
                continue;
            }

            corrections.Add(new TermCorrection(
                term.Source,
                term.Accepted,
                settled,
                "already rendered this way earlier in the document",
                TermAction.Converged));

            text = converged;
        }

        return new TerminologyResult(text, corrections);
    }

    /// <summary>
    /// Records a rendering the reader chose, so the rest of the document follows
    /// it rather than the table.
    /// </summary>
    public void Prefer(string sourceTerm, string rendering) => _chosen[sourceTerm] = rendering;

    public IReadOnlyDictionary<string, string> Settled => _chosen;
}
