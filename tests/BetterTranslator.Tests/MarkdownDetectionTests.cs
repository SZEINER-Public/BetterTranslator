using BetterTranslator.Engine.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Which messages get the Markdown path, and -- more importantly -- which do
/// not.
///
/// Every string is valid Markdown to a parser, so "does it parse" is not a
/// question worth asking. The question is whether the author reached for a
/// syntax character, because that is what decides whether a View/Source switch
/// appears over an ordinary sentence.
/// </summary>
public sealed class MarkdownDetectionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("Just a plain sentence.")]
    [InlineData("The build failed on the second stage\nbecause the cache was cold.")]
    [InlineData("One paragraph about the build.\n\nAnd a second one about the cache.")]
    // Found by the parser, not written by the author. A pasted sentence with a
    // link in it is the commonest message there is and must stay plain.
    [InlineData("Visit https://bubble.example.com for the changelog.")]
    // Arithmetic, not emphasis: CommonMark needs the delimiter against a word.
    [InlineData("The retry budget is 5 * 3 * 2 attempts.")]
    // Raw HTML, inline and as a block. CommonMark accepts both, and there is
    // nothing to render either as: a renderer of headings and lists has no view
    // of a <span> or a <context> tag that differs from the source. Counting them
    // put a View/Source switch on a pasted brief whose View was blank.
    [InlineData("Text with <span>inline html</span> in it.")]
    [InlineData("<context>\nPROJECT: A desktop translation tool.\n</context>")]
    public void Prose_is_not_Markdown(string text) =>
        MarkdownSyntax.HasStructure(text).Should().BeFalse();

    [Theory]
    [InlineData("# Release notes")]
    [InlineData("## Requirements")]
    [InlineData("- Windows 11\n- macOS 14")]
    [InlineData("1. Install\n2. Restart")]
    [InlineData("- [ ] Document the retry budget")]
    [InlineData("Keeps your workspace **in sync**.")]
    [InlineData("Keeps your workspace *in sync*.")]
    [InlineData("Rewrote it on `FileSystemWatcher`.")]
    [InlineData("> Upgrading needs no migration.")]
    [InlineData("| Setting | Default |\n| --- | --- |\n| Watch | on |")]
    [InlineData("See the [changelog](https://bubble.example.com/log).")]
    [InlineData("![The sync indicator](https://bubble.example.com/sync.png)")]
    [InlineData("```bash\nbubble sync\n```")]
    [InlineData("---\ntitle: Release notes\n---\n\nBody text.")]
    public void Authored_syntax_is_Markdown(string text) =>
        MarkdownSyntax.HasStructure(text).Should().BeTrue();

    [Fact]
    public void A_thematic_break_is_structure_even_though_it_carries_no_prose()
    {
        MarkdownSyntax.HasStructure("Before.\n\n---\n\nAfter.").Should().BeTrue();

        // And it survives a round trip with nothing to translate in it.
        MarkdownSegmenter.Segment("Before.\n\n---\n\nAfter.").Should().HaveCount(2);
    }
}
