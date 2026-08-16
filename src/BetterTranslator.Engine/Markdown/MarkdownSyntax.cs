using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BetterTranslator.Engine.Markdown;

/// <summary>
/// The one Markdown pipeline the translation path uses, and the question of
/// whether a message is Markdown at all.
/// </summary>
public static class MarkdownSyntax
{
    /// <summary>
    /// Three extensions, each load-bearing.
    ///
    /// Advanced is what makes pipe tables and task lists parse at all. Front
    /// matter has to be an extension or `---` opens a thematic break and the
    /// YAML underneath becomes a paragraph -- which the translator would then
    /// happily translate. Precise source location is what gives inlines real
    /// spans, and spans are the whole mechanism: the answer is spliced into the
    /// original text rather than rendered back out of a tree.
    /// </summary>
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UsePreciseSourceLocation()
        .Build();

    public static MarkdownDocument Parse(string markdown) =>
        Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);

    /// <summary>
    /// True when the message was written as Markdown rather than merely being
    /// text a Markdown parser will accept -- which is all text.
    ///
    /// The rule is "did anything here need a syntax character to say it". Prose
    /// in several paragraphs is still prose; a heading, a list, a fence, a
    /// table, a quote, emphasis, a code span or a written link is not. A bare
    /// URL is deliberately not counted: pasting a sentence with a link in it is
    /// not authoring Markdown, and treating it as such would put a View/Source
    /// switch on half the ordinary messages in the application.
    /// </summary>
    public static bool HasStructure(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return false;
        }

        var document = Parse(markdown);

        foreach (var block in document)
        {
            // Front matter is a block of its own, and it only exists when the
            // author wrote the fence for it.
            if (block is YamlFrontMatterBlock)
            {
                return true;
            }

            // Raw HTML is not authored Markdown formatting and there is nothing
            // to render it as. A brief wrapped in angle-bracket tags is one
            // HtmlBlock from end to end: calling that Markdown offered a
            // View/Source switch whose View was empty, because a renderer of
            // headings and lists has nothing to say about a `<context>` tag.
            if (block is HtmlBlock)
            {
                continue;
            }

            if (block is not ParagraphBlock paragraph)
            {
                return true;
            }

            if (IsMarked(paragraph.Inline))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the Markdown path has anything to do with this message.
    ///
    /// Looking like Markdown and holding prose are different questions, and the
    /// second one is the one the translator has to ask. A message that is
    /// nothing but a fenced block, or a brief wrapped in angle-bracket tags that
    /// CommonMark reads as raw HTML, is structure from end to end: the segmenter
    /// protects all of it and offers nothing, and a path with nothing to send
    /// reports a failure for a message that was never given a chance. Those go
    /// the ordinary way instead.
    ///
    /// The rendering question is separate and stays with <see cref="HasStructure"/>:
    /// a pasted fenced block still renders as one and still gets its
    /// View/Source switch.
    /// </summary>
    public static bool HasTranslatableProse(string? markdown) =>
        HasStructure(markdown) && MarkdownSegmenter.Segment(markdown ?? string.Empty).Count > 0;

    private static bool IsMarked(ContainerInline? container)
    {
        if (container is null)
        {
            return false;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline:
                case LineBreakInline:
                case HtmlEntityInline:

                // Same reason as a raw HTML block: the renderer has nothing to
                // show for a `<span>`, so a message whose only markup is one
                // would get a rendered view identical to its source.
                case HtmlInline:
                    break;

                // Found by the autolink extension rather than written, so it
                // says nothing about how the message was authored.
                case LinkInline { IsAutoLink: true }:
                    break;

                default:
                    return true;
            }
        }

        return false;
    }
}
