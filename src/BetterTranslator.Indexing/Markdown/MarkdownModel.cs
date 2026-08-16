namespace BetterTranslator.Indexing.Markdown;

/// <summary>
/// A run of text with the marks that apply to it. WPF has no Markdown renderer,
/// so the preview parses to this block model and renders it through typed
/// DataTemplates styled from the token dictionary.
/// </summary>
/// <param name="Link">
/// Where the run points, on the runs that came out of a link. Null everywhere
/// else, which is what the renderer reads to decide whether a run is a
/// hyperlink or ordinary text.
/// </param>
public sealed record MdInline(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    string? Link = null);

public abstract record MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <summary>
/// One item of a list.
/// </summary>
/// <param name="Marker">
/// What stands in the gutter -- a bullet, or the item's own number. Computed
/// here rather than in the template because an ItemsControl does not hand its
/// template the index, and an ordered list that renders as bullets has lost the
/// only thing that made it ordered.
/// </param>
/// <param name="Children">
/// Blocks nested under the item, which is how a list inside a list arrives.
/// Empty for a flat item.
/// </param>
/// <param name="Done">
/// The state of a task item's box, or null when the item is not a task. Null and
/// false are different things: an unticked box is still a box.
/// </param>
public sealed record MdListItem(
    string Marker,
    IReadOnlyList<MdInline> Inlines,
    IReadOnlyList<MdBlock> Children,
    bool? Done = null);

public sealed record MdBulletList(IReadOnlyList<MdListItem> Items, bool Ordered = false) : MdBlock;

public sealed record MdCodeBlock(string Text, string? Language = null) : MdBlock;

/// <summary>
/// A pipe table. Requires UseAdvancedExtensions on the Markdig pipeline; the
/// first row is the header when <see cref="HasHeader"/> is set.
/// </summary>
public sealed record MdTable(IReadOnlyList<IReadOnlyList<string>> Rows, bool HasHeader) : MdBlock;

public sealed record MdThematicBreak : MdBlock;

/// <summary>A quoted passage, rendered as an inset block.</summary>
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
