using System.IO;
using System.Linq;
using BetterTranslator.Engine.Corpus;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Which files of a folder become corpus.
///
/// The leading-<c>**/</c> case has its own test because it is the one that was
/// wrong in the reference for a long time: an exclude rule every project relies
/// on silently indexed the whole of vendor, and only a nested fixture tree would
/// have hidden it.
/// </summary>
public sealed class CorpusSelectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-corpus-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Given(string relative, string text = "content")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ADoubleStarPrefixMeansZeroOrMoreDirectories()
    {
        // The bug: "**/vendor/**" translated to ".*/vendor/.*" requires
        // something before the slash, so vendor at the ROOT was not excluded.
        CorpusSelector.Matches("vendor/x.md", ["**/vendor/**"]).Should().BeTrue();
        CorpusSelector.Matches("lib/vendor/x.md", ["**/vendor/**"]).Should().BeTrue();
        CorpusSelector.Matches("a/b/vendor/deep/x.md", ["**/vendor/**"]).Should().BeTrue();

        CorpusSelector.Matches("vendors/x.md", ["**/vendor/**"]).Should().BeFalse();
        CorpusSelector.Matches("src/x.md", ["**/vendor/**"]).Should().BeFalse();
    }

    [Fact]
    public void ATrailingDoubleStarMatchesTheDirectoryItselfAndEverythingUnderIt()
    {
        CorpusSelector.Matches("docs", ["docs/**"]).Should().BeTrue();
        CorpusSelector.Matches("docs/a/b.md", ["docs/**"]).Should().BeTrue();
        CorpusSelector.Matches("documents/a.md", ["docs/**"]).Should().BeFalse();
    }

    [Fact]
    public void ASingleStarStaysInsideOneSegment()
    {
        CorpusSelector.Matches("a.md", ["*.md"]).Should().BeTrue();
        CorpusSelector.Matches("docs/a.md", ["*.md"]).Should().BeFalse("a single star does not cross a slash");
        CorpusSelector.Matches("docs/a.md", ["**/*.md"]).Should().BeTrue();
    }

    [Fact]
    public void BackslashesAreTheSamePathAsForwardSlashes()
    {
        CorpusSelector.Matches(@"docs\a.md", ["docs/**"]).Should().BeTrue();
        CorpusSelector.Matches("docs/a.md", [@"docs\**"]).Should().BeTrue();
    }

    [Fact]
    public void AnEmptyOrMissingGlobListMatchesNothing()
    {
        CorpusSelector.Matches("a.md", []).Should().BeFalse();
        CorpusSelector.Matches("a.md", null).Should().BeFalse();
        CorpusSelector.Matches("a.md", [""]).Should().BeFalse();
    }

    [Fact]
    public void ExcludeBeatsInclude()
    {
        Given("src/a.md");
        Given("vendor/b.md");
        Given("src/vendor/c.md");

        var selected = CorpusSelector.Select(_root, include: ["**"], exclude: ["**/vendor/**"]);

        selected.Select(f => f.Relative).Should().BeEquivalentTo(["src/a.md"]);
    }

    [Fact]
    public void OnlyTypesAReaderExistsForAreSelected()
    {
        Given("a.md");
        Given("b.exe");
        Given("c.txt");

        CorpusSelector.Select(_root).Select(f => f.Relative)
            .Should().BeEquivalentTo(["a.md", "c.txt"]);

        // An empty type list means the caller filters types itself, which is how
        // the indexer uses it -- the readers are the authority there.
        CorpusSelector.Select(_root, types: []).Should().HaveCount(3);
    }

    [Fact]
    public void AFileTooLargeToBeProseIsSkipped()
    {
        Given("small.md");
        Given("huge.md", new string('x', 4096));

        var selected = CorpusSelector.Select(_root, maxFileMegabytes: 0.001);

        selected.Select(f => f.Relative).Should().BeEquivalentTo(["small.md"]);
    }

    [Fact]
    public void AMissingRootIsEmptyRatherThanAThrow()
    {
        CorpusSelector.Select(Path.Combine(_root, "nope")).Should().BeEmpty();
    }

    [Fact]
    public void EachSelectedFileCarriesTheRelativePathTheRulesMatchedOn()
    {
        Given("docs/guide/a.md");

        var file = CorpusSelector.Select(_root).Single();

        file.Relative.Should().Be("docs/guide/a.md", "forward slashes, so a glob written once works on any host");
        file.Extension.Should().Be(".md");
        file.Length.Should().BeGreaterThan(0);
        File.Exists(file.FullPath).Should().BeTrue();
    }
}
