using System.IO;
using System.Linq;
using BetterTranslator.Engine.Memory;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Re-testing stored memory against the current gates.
///
/// An exact memory hit is returned verbatim without a model call, which also
/// means without passing back through the gates. Memory therefore inherits
/// whatever the gates missed on the day an entry was written, and a gate fixed
/// afterwards does not clean up behind itself.
/// </summary>
public sealed class MemoryAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-audit-" + Guid.NewGuid().ToString("N"));

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

    private TranslationMemory Given(params MemoryPair[] pairs)
    {
        var memory = TranslationMemory.Load(Store);
        memory.Add(pairs);
        return TranslationMemory.Load(Store);
    }

    /// <summary>
    /// The entry that is the reason this exists: a one-word cell reading "One"
    /// came back as forty words about choosing a domain name, and every gate of
    /// the day accepted it.
    /// </summary>
    private static MemoryPair TheEntryThatStartedIt() => new()
    {
        Source = "One",
        Target = "Vyberte si doménové jméno, které je krátké, snadno zapamatovatelné a "
            + "vystihuje povahu vašeho podnikání, protože právě ono bude to první, "
            + "co návštěvníci uvidí a co si zapamatují na dlouhou dobu dopředu.",
    };

    [Fact]
    public void AnEntryTheGatesNowRejectIsFound()
    {
        var memory = Given(TheEntryThatStartedIt());

        var report = MemoryAudit.Audit(memory);

        report.Checked.Should().Be(1);
        report.IsClean.Should().BeFalse();
        report.InventionCount.Should().Be(1);
        report.Defects.Single().Kind.Should().Be(MemoryDefectKind.Invention);
    }

    [Fact]
    public void AGoodEntryIsLeftAlone()
    {
        var memory = Given(new MemoryPair { Source = "The build failed.", Target = "Sestavení selhalo." });

        MemoryAudit.Audit(memory).IsClean.Should().BeTrue();
    }

    [Fact]
    public void TheReportCarriesNoValues()
    {
        // A memory store is payload. Reasons are masked so a report cannot
        // become a listing of content.
        var memory = Given(TheEntryThatStartedIt());

        var report = MemoryAudit.Audit(memory);

        foreach (var defect in report.Defects)
        {
            defect.Reason.Should().NotContain("doménové");
            defect.Reason.Should().NotContainAny("0", "1", "2", "3", "4", "5", "6", "7", "8", "9");
        }

        report.Summary.Single().Should().StartWith("1 x ").And.Contain("was invented");
    }

    [Fact]
    public void IdenticalFailuresCollapseIntoOneCountedLine()
    {
        var memory = Given(
            TheEntryThatStartedIt(),
            new MemoryPair
            {
                Source = "Two",
                Target = "Zvolte si poskytovatele hostingu, který nabízí dostatečný výkon, "
                    + "spolehlivou dostupnost a rychlou podporu, protože na tom závisí "
                    + "rychlost i stabilita celého vašeho webu po celou dobu provozu.",
            });

        var report = MemoryAudit.Audit(memory);

        report.Defects.Should().HaveCount(2);
        report.Summary.Should().ContainSingle().Which.Should().StartWith("2 x ");
    }

    [Fact]
    public void MaskingRemovesTheQuotedFragmentAndTheNumbers()
    {
        MemoryAudit.Mask("answer has 40 words for a 1-word source, content was invented")
            .Should().Be("answer has # words for a #-word source, content was invented");

        MemoryAudit.Mask("answer narrates the task instead of translating: 'Here is the Czech'")
            .Should().Be("answer narrates the task instead of translating: '...'");
    }

    [Fact]
    public void RepairRemovesTheInventionAndBacksUpFirst()
    {
        var memory = Given(
            new MemoryPair { Source = "The build failed.", Target = "Sestavení selhalo." },
            TheEntryThatStartedIt());

        var report = MemoryAudit.Audit(memory);

        MemoryAudit.Repair(memory, report).Should().Be(1);

        File.Exists(Store + ".bak").Should().BeTrue("this is the one write in the memory path that is not an append");
        File.ReadAllLines(Store + ".bak").Should().HaveCount(2, "the backup is the store as it was");

        var reloaded = TranslationMemory.Load(Store);
        reloaded.Count.Should().Be(1);
        reloaded.FindExact("The build failed.").Should().NotBeNull();
        reloaded.FindExact("One").Should().BeNull();
    }

    [Fact]
    public void RemovalIsNarrowerThanReportingUnlessAskedOtherwise()
    {
        // A structural failure on human-approved text is more likely a false
        // positive than a defect, so reporting it and deleting it are not the
        // same decision.
        var memory = Given(new MemoryPair { Source = "Open `Settings` and pin it.", Target = "Otevřete a připněte." });

        var report = MemoryAudit.Audit(memory);

        report.IsClean.Should().BeFalse("a code span went missing");
        report.Defects.Single().Kind.Should().Be(MemoryDefectKind.Structure);

        MemoryAudit.Repair(memory, report).Should().Be(0, "structure alone does not earn removal");
        TranslationMemory.Load(Store).Count.Should().Be(1);

        MemoryAudit.Repair(memory, report, allReasons: true).Should().Be(1);
        TranslationMemory.Load(Store).Count.Should().Be(0);
    }

    [Fact]
    public void ACleanStoreIsNeverRewritten()
    {
        var memory = Given(new MemoryPair { Source = "The build failed.", Target = "Sestavení selhalo." });

        MemoryAudit.Repair(memory, MemoryAudit.Audit(memory)).Should().Be(0);

        File.Exists(Store + ".bak").Should().BeFalse("nothing was at risk, so nothing was backed up");
    }

    [Fact]
    public void EntriesAreTestedAtTheRatioTheWritersUsed()
    {
        // Testing at the gate's stricter default would report entries that were
        // legitimately accepted under the rule in force when they were written --
        // a report about the audit's settings rather than about the memory.
        MemoryAudit.WritersMaxLengthRatio.Should()
            .BeGreaterThan(BetterTranslator.Engine.Markup.OutputLeak.DefaultMaxLengthRatio);
    }
}
