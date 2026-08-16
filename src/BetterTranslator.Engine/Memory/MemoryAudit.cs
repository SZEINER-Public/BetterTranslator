using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BetterTranslator.Engine.Markup;

namespace BetterTranslator.Engine.Memory;

/// <summary>Which gate an entry failed, which decides whether it is removed.</summary>
public enum MemoryDefectKind
{
    /// <summary>
    /// The gate that had the hole: an answer longer than its source can be.
    /// </summary>
    Invention,

    /// <summary>Markup, placeholders or protected blocks that moved.</summary>
    Structure,

    /// <summary>Anything else the gates catch.</summary>
    Other,
}

/// <summary>One stored entry that no longer passes. Carries no values.</summary>
public sealed record MemoryDefect
{
    /// <summary>Which line of the store. Enough to find it; not its content.</summary>
    public required int Row { get; init; }

    /// <summary>The gate's reason, with content and numbers masked out.</summary>
    public required string Reason { get; init; }

    public required MemoryDefectKind Kind { get; init; }
}

/// <summary>What an audit found. Counts and reasons, never text.</summary>
public sealed record MemoryAuditReport
{
    public required int Checked { get; init; }

    public required IReadOnlyList<MemoryDefect> Defects { get; init; }

    public int InventionCount => Defects.Count(d => d.Kind == MemoryDefectKind.Invention);

    public bool IsClean => Defects.Count == 0;

    /// <summary>
    /// One line per distinct reason with its count -- a summary, which is the
    /// only shape this report is allowed to take.
    /// </summary>
    public IReadOnlyList<string> Summary =>
    [
        .. Defects
            .GroupBy(d => d.Reason, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} x {g.Key}"),
    ];
}

/// <summary>
/// Re-tests stored memory against the current gates. Ported from
/// `rag-memory-verify.ps1`.
///
/// WHY THIS HAS TO EXIST. An exact memory hit is returned verbatim without a
/// model call -- which also means without passing back through the gates. That
/// is the whole point: reuse is instant because nothing re-examines it. The
/// consequence is that memory inherits whatever the gates missed on the day an
/// entry was written, and a gate fixed afterwards does not clean up behind
/// itself, so one bad answer keeps being served, cheaply, forever.
///
/// This happened, and is the reason the script exists. The word-count invention
/// gate applied only to sources of eight words or more, so a one-word table cell
/// had no ceiling at all: a cell reading "One" came back as forty words about
/// choosing a domain name, passed every gate, and was learned. The gate is fixed
/// -- but the entry it let through is still in memory, and only a pass like this
/// one takes it back out.
///
/// Values are never returned. A memory store is payload, and payload text does
/// not reach an operator's screen. Reasons are masked so a report cannot become
/// a listing of content.
/// </summary>
public static class MemoryAudit
{
    /// <summary>
    /// The ratio the WRITERS used, not the gate's stricter default. The document
    /// translators accept up to this before calling an answer over-long; testing
    /// stored entries at the gate's 1.9 reports entries that were legitimately
    /// accepted under the rule in force when they were written, which is a
    /// report about this method's settings rather than about the memory.
    /// </summary>
    public const double WritersMaxLengthRatio = 2.6;

    /// <summary>Quoted fragments the gates put in their reasons.</summary>
    private static readonly Regex Quoted = new("'[^']*'", RegexOptions.Compiled);

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// Re-runs the gates over every entry. Reports everything; deciding what to
    /// remove is a separate call, because reporting and deleting must not be the
    /// same decision.
    /// </summary>
    public static MemoryAuditReport Audit(
        TranslationMemory memory,
        double maxLengthRatio = WritersMaxLengthRatio,
        IReadOnlyList<string>? doNotTranslate = null)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var defects = new List<MemoryDefect>();

        foreach (var entry in memory.Entries)
        {
            var leak = OutputLeak.Check(entry.Source, entry.Target, prompt: null, maxLengthRatio);

            if (leak is not null)
            {
                defects.Add(Defect(entry.Row, leak));
                continue;
            }

            var structure = ChunkIntegrity.Check(
                entry.Source, entry.Target, doNotTranslate: doNotTranslate, minLengthRatio: 0.35);

            if (structure is not null)
            {
                defects.Add(Defect(entry.Row, structure));
            }
        }

        return new MemoryAuditReport
        {
            Checked = memory.Count,
            Defects = defects,
        };
    }

    private static MemoryDefect Defect(int row, string reason) => new()
    {
        Row = row,
        Reason = Mask(reason),
        Kind = reason.Contains("was invented", StringComparison.Ordinal)
            ? MemoryDefectKind.Invention
            : MemoryDefectKind.Structure,
    };

    /// <summary>
    /// Strips the content out of a gate's reason. The gates quote the fragment
    /// they objected to, which is exactly the text this report must not carry;
    /// numbers go too, so identical failures collapse into one counted line.
    /// </summary>
    public static string Mask(string reason) =>
        Digits.Replace(Quoted.Replace(reason, "'...'"), "#");

    /// <summary>
    /// Rewrites the store without the failing entries, after copying the original
    /// beside it as <c>.bak</c>. Returns how many were removed.
    ///
    /// Removal is narrower than reporting on purpose: by default only entries the
    /// INVENTION gate catches are removed, because that is the gate that had the
    /// hole. The other gates have been in place all along, so an entry failing
    /// one of those is more likely a false positive on human-approved text --
    /// which is most of a mature store -- than a genuine defect.
    /// </summary>
    /// <param name="allReasons">Remove every failure, not only invention.</param>
    public static int Repair(
        TranslationMemory memory,
        MemoryAuditReport report,
        bool allReasons = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(report);

        if (memory.Path is null)
        {
            throw new InvalidOperationException("memory has no path to rewrite");
        }

        var doomed = report.Defects
            .Where(d => allReasons || d.Kind == MemoryDefectKind.Invention)
            .Select(d => d.Row)
            .ToHashSet();

        if (doomed.Count == 0)
        {
            return 0;
        }

        // The backup goes first. This is the one operation in the whole memory
        // path that is not append-only, so the original has to survive a crash
        // halfway through the rewrite.
        if (File.Exists(memory.Path))
        {
            File.Copy(memory.Path, memory.Path + ".bak", overwrite: true);
        }

        var kept = memory.Entries
            .Where(e => !doomed.Contains(e.Row))
            .OrderBy(e => e.Row)
            .ToList();

        var text = new StringBuilder();

        foreach (var entry in kept)
        {
            text.Append(JsonSerializer.Serialize(entry, Compact)).Append('\n');
        }

        // Rewritten through a temporary file so an interrupted write cannot
        // leave a half-length store where a complete one used to be. No BOM: the
        // reader would see it as part of the first entry.
        var staged = memory.Path + ".tmp";
        File.WriteAllText(staged, text.ToString(), new UTF8Encoding(false));
        File.Move(staged, memory.Path, overwrite: true);

        return doomed.Count;
    }

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
