namespace BetterTranslator.Core.Verification.Checks.Structure;

public sealed class MarkdownParityCheck : StructureCheck
{
    public override string CheckId => Checks.CheckId.Structure.MarkdownParity;

    protected override void Find(CheckContext context, List<CheckFinding> findings)
    {
        DiffNodes(
            context,
            [.. context.Source.OfKind(DocumentNodeKind.Heading)],
            [.. context.Target.OfKind(DocumentNodeKind.Heading)],
            node => "level " + node.Attribute("level"),
            "heading",
            findings);

        DiffNodes(
            context,
            [.. context.Source.OfKind(DocumentNodeKind.CodeFence)],
            [.. context.Target.OfKind(DocumentNodeKind.CodeFence)],
            node => node.Attribute("fence") + " " + node.Attribute("language"),
            "code fence",
            findings);

        DiffNodes(
            context,
            [.. context.Source.OfKind(DocumentNodeKind.List)],
            [.. context.Target.OfKind(DocumentNodeKind.List)],
            node => (node.Attribute("ordered") == "true" ? "ordered " : "bullet ") + node.Attribute("items") + " items",
            "list",
            findings);

        DiffNodes(
            context,
            [.. context.Source.OfKind(DocumentNodeKind.BlockQuote)],
            [.. context.Target.OfKind(DocumentNodeKind.BlockQuote)],
            node => "depth " + node.Attribute("depth"),
            "blockquote",
            findings);

        DiffNodes(
            context,
            [.. context.Source.OfKind(DocumentNodeKind.Link)],
            [.. context.Target.OfKind(DocumentNodeKind.Link)],
            node => node.Attribute("url"),
            "link",
            findings);

        DiffNodes(
            context,
            [.. context.Source.OfKind(DocumentNodeKind.Image)],
            [.. context.Target.OfKind(DocumentNodeKind.Image)],
            node => node.Attribute("url"),
            "image",
            findings);
    }
}
