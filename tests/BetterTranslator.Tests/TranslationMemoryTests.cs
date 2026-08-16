using System.IO;
using System.Linq;
using BetterTranslator.Engine.Memory;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Translation memory: the project's own already-approved translation.
///
/// An exact hit returns the approved target verbatim -- instant, free, and
/// consistent by construction, which is the one property a model cannot give
/// you. The care in these tests is mostly about what must NOT be admitted: an
/// entry is not just stored, it is promoted to the model as an example to
/// imitate, so a bad entry does not sit harmlessly in a file, it teaches.
/// </summary>
public sealed class TranslationMemoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-tm-" + Guid.NewGuid().ToString("N"));

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

    private string Store => Path.Combine(_root, "cs.jsonl");

    [Fact]
    public void TheSameSentenceWrappedDifferentlyIsTheSameSentence()
    {
        // A document routinely contains both.
        TranslationMemory.KeyFor("the  build\n  failed").Should().Be(TranslationMemory.KeyFor("the build failed"));
    }

    [Fact]
    public void AnExactHitReturnsTheApprovedTargetVerbatim()
    {
        var memory = TranslationMemory.Load(Store);

        memory.Add([new MemoryPair { Source = "The build failed.", Target = "Sestavení selhalo." }])
            .Should().Be(1);

        memory.FindExact("The   build failed.")!.Target.Should().Be("Sestavení selhalo.");
        memory.FindExact("Something else").Should().BeNull();
    }

    [Fact]
    public void AppendingNeverRewritesAndSurvivesAReload()
    {
        var first = TranslationMemory.Load(Store);
        first.Add([new MemoryPair { Source = "one", Target = "jedna" }]);

        var second = TranslationMemory.Load(Store);
        second.Add([new MemoryPair { Source = "two", Target = "dva" }]);

        var reloaded = TranslationMemory.Load(Store);

        reloaded.Count.Should().Be(2);
        reloaded.FindExact("one")!.Target.Should().Be("jedna");
        reloaded.FindExact("two")!.Target.Should().Be("dva");
    }

    [Fact]
    public void TheFirstEntryForASourceWins()
    {
        // The file is append-ordered, so the oldest approved rendering is the
        // established one.
        var memory = TranslationMemory.Load(Store);
        memory.Add([new MemoryPair { Source = "store", Target = "úložiště" }]);
        memory.Add([new MemoryPair { Source = "store", Target = "sklad" }]).Should().Be(0);

        TranslationMemory.Load(Store).FindExact("store")!.Target.Should().Be("úložiště");
    }

    [Fact]
    public void ACorruptLineCostsOneEntryRatherThanTheFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(Store,
        [
            """{"src":"one","tgt":"jedna"}""",
            "{ this is not json",
            """{"src":"two","tgt":"dva"}""",
        ]);

        var memory = TranslationMemory.Load(Store);

        memory.Count.Should().Be(2);
        memory.Malformed.Should().Be(1);
    }

    [Fact]
    public void NoByteOrderMarkIsEverWritten()
    {
        // A BOM makes the first parse fail with a column-1 error that reads like
        // malformed data.
        var memory = TranslationMemory.Load(Store);
        memory.Add([new MemoryPair { Source = "one", Target = "jedna" }]);

        File.ReadAllBytes(Store).Take(3).Should().NotEqual([(byte)0xEF, (byte)0xBB, (byte)0xBF]);
    }

    [Fact]
    public void AlignedDocumentsBecomeMemoryButOnlyTheLinesThatEarnIt()
    {
        const string Source = """
            # Heading that is long enough to count as prose here
            ```
            code stays out of it entirely
            ```
            This sentence is properly translated into another language.
            Identical line on both sides.
            short
            """;

        const string Target = """
            # Nadpis, který je dostatečně dlouhý, aby se počítal jako próza
            ```
            code stays out of it entirely
            ```
            Tato věta je řádně přeložena do jiného jazyka.
            Identical line on both sides.
            short
            """;

        var pairs = TranslationMemory.FromAligned(Source, Target, "readme.md");

        output.WriteLine(string.Join("\n", pairs.Select(p => $"{p.Key}: {p.Source} -> {p.Target}")));

        pairs.Should().HaveCount(2, "the heading and the sentence; nothing else earns a place");
        pairs.Should().NotContain(p => p.Source.Contains("code stays", StringComparison.Ordinal), "fenced code is not prose");
        pairs.Should().NotContain(p => p.Source.Contains("Identical", StringComparison.Ordinal), "an unchanged line was never translated");
        pairs.Should().NotContain(p => p.Source == "short", "too few letters to be worth remembering");
        pairs.Should().OnlyContain(p => p.Origin == "readme.md" && p.Key.StartsWith('L'));
    }

    [Fact]
    public void AMostlyEnglishTargetIsRefusedThoughEveryOtherGatePassesIt()
    {
        // One translated word with the English left standing preserves every code
        // span, keeps its length and leaks nothing. As an exemplar it teaches the
        // model to do exactly that, which is why the word-set check exists.
        const string Source = "Každá invocation writes its own output to the daemon log file";
        const string Target = "Kazda invocation writes its own output to the daemon log file";

        TranslationMemory.FromAligned(Source, Target).Should().BeEmpty();
    }

    [Fact]
    public void MisalignedDocumentsAreRefusedRatherThanPairedWrongly()
    {
        var act = () => TranslationMemory.FromAligned("one\ntwo", "jedna");

        act.Should().Throw<ArgumentException>().WithMessage("*line counts differ*");
    }
}
