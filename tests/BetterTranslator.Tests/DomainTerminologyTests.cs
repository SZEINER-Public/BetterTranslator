using System.Linq;
using BetterTranslator.Engine.Terminology;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The domain vocabulary, and the homograph class it exists for.
///
/// Measured: an English brief naming the source project `Engine` came back with
/// `motor`, the machine in a car. Nothing was misspelled, the Czech was
/// grammatical, and no gate could see it.
/// </summary>
public sealed class DomainTerminologyTests
{
    private static DomainTermTable Table() => DomainTerms.For();

    [Fact]
    public void TheShippedTableLoadsFromTheEmbeddedResource()
    {
        var table = Table();

        table.Terms.Should().NotBeEmpty("the table is embedded and must be found at runtime");
        table.Terms.Should().Contain(t => t.Source == "engine");
        table.Terms.Should().Contain(t => t.Source == "Engine" && t.KeepInSource);
    }

    [Fact]
    public void TheMeasuredDefectIsCorrected()
    {
        var result = TerminologyCorrector.Apply(
            "Survey Core, Engine, Runtime, and Indexing to locate the entry points.",
            "Prozkoumejte jádro, motor, prostředí a indexování.",
            Table());

        result.Text.Should().NotContain("motor");
        result.Text.Should().Contain("Engine");
        result.Corrections.Should().Contain(c => c.From == "motor" && c.Action == TermAction.SourceRestored);
    }

    [Theory]
    [InlineData("The file is on disk.", "Pilník je na disku.", "pilník", "soubor")]
    [InlineData("Install the driver.", "Nainstalujte řidiče.", "řidič", "ovladač")]
    [InlineData("One thread per request.", "Jedna nit na požadavek.", "nit", "vlákno")]
    [InlineData("Check the scope.", "Zkontrolujte dalekohled.", "dalekohled", "rozsah")]
    [InlineData("Create a branch.", "Vytvořte pobočku.", "pobočka", "větev")]
    public void AHomographRenderedTheEverydayWayIsCorrected(
        string source,
        string translated,
        string wrong,
        string right)
    {
        var result = TerminologyCorrector.Apply(source, translated, Table());

        result.Text.ToLowerInvariant().Should().NotContain(wrong);
        result.Text.ToLowerInvariant().Should().Contain(right);
        result.Corrections.Should().ContainSingle().Which.Action.Should().Be(TermAction.WrongRenderingReplaced);
    }

    [Fact]
    public void ACorrectionCarriesTheReasonTheReaderWillSee()
    {
        var result = TerminologyCorrector.Apply("Install the driver.", "Nainstalujte řidiče.", Table());

        result.Corrections.Single().Reason.Should().NotBeEmpty();
        result.Corrections.Single().Source.Should().Be("driver");
    }

    [Fact]
    public void AnAlreadyCorrectRenderingIsLeftAlone()
    {
        const string Good = "Ovladač je nainstalován.";

        var result = TerminologyCorrector.Apply("The driver is installed.", Good, Table());

        result.Text.Should().Be(Good);
        result.Changed.Should().BeFalse();
    }

    [Fact]
    public void ATermTheSourceNeverUsedIsNeverTouched()
    {
        // The everyday word in an everyday sentence. Correcting it would be the
        // corrector inventing a defect.
        const string Unrelated = "Řidič autobusu zastavil.";

        TerminologyCorrector.Apply("The bus stopped.", Unrelated, Table())
            .Text.Should().Be(Unrelated);
    }

    [Fact]
    public void TheInflectedTailOfAWrongRenderingGoesWithIt()
    {
        var result = TerminologyCorrector.Apply("The engine failed.", "Selhání motoru bylo hlášeno.", Table());

        result.Text.Should().NotContain("motoru");
        result.Text.Should().NotContain("motor");
    }

    [Fact]
    public void SentenceInitialCaseIsKept()
    {
        var result = TerminologyCorrector.Apply("The driver failed.", "Řidič selhal.", Table());

        result.Text.Should().StartWith("Ovladač");
    }

    [Fact]
    public void ATermSwappedIntoAShoutingLineShoutsWithIt()
    {
        // CasingGuard runs first and uppercases a whole answer whose source was
        // all capitals, so the corrector lands in a line of capitals.
        var cased = Engine.Markup.CasingGuard.Restore("DO NOT BREAK THE ENGINE", "nerozbijte motor");

        TerminologyCorrector.Apply("DO NOT BREAK THE ENGINE", cased, Table())
            .Text.Should().Be("NEROZBIJTE ENGINE");
    }

    [Fact]
    public void AWordThatMerelyContainsATermIsNotAMatch()
    {
        // "port" inside "import" is the failure class whole-word matching exists
        // for.
        DomainTermTable.Contains("import", "port").Should().BeFalse();
        DomainTermTable.Contains("the port is open", "port").Should().BeTrue();
    }

    [Fact]
    public void ACzechEndingAfterATermStillCountsAsThatTerm() =>
        DomainTermTable.Contains("v úložišti", "úložišt").Should().BeTrue();

    [Fact]
    public void TheTableParsesTheFourColumnsPositionally()
    {
        var table = DomainTerms.Parse(
            "| English | Czech | Wrong renderings | Why |\n"
            + "|---|---|---|---|\n"
            + "| widget | udělátko | mašinka; věcička | a UI element |\n"
            + "| Gadget | - | udělátko | a product name |\n");

        table.Terms.Should().HaveCount(2);

        var widget = table.Terms[0];
        widget.Accepted.Should().Be("udělátko");
        widget.Wrong.Should().Equal("mašinka", "věcička");
        widget.Reason.Should().Be("a UI element");
        widget.KeepInSource.Should().BeFalse();

        table.Terms[1].KeepInSource.Should().BeTrue("a dash marks do-not-translate");
    }

    [Fact]
    public void OneRenderingPerTermSurvivesTheWholeDocument()
    {
        var session = new TerminologySession(Table());

        var first = session.Apply("Open the file.", "Otevřete soubor.");
        var second = session.Apply("Delete the file.", "Smažte pilník.");

        first.Text.Should().Be("Otevřete soubor.");
        second.Text.Should().Contain("soubor");
        second.Text.Should().NotContain("pilník");
    }

    [Fact]
    public void AReadersChoiceOverridesTheTableForTheRestOfTheDocument()
    {
        var session = new TerminologySession(Table());
        session.Prefer("branch", "branch");

        var result = session.Apply("Create a branch.", "Vytvořte větev.");

        result.Text.Should().Contain("branch");
        result.Corrections.Should().Contain(c => c.Action == TermAction.Converged);
    }

    [Fact]
    public void ASessionThatSettledNothingChangesNothing()
    {
        var session = new TerminologySession(Table());

        session.Apply("Open the file.", "Otevřete soubor.").Changed.Should().BeFalse();
        session.Settled.Should().ContainKey("file");
    }

    [Fact]
    public void EmptyInputIsNotAFailure()
    {
        DomainTerms.Parse(null).Terms.Should().BeEmpty();
        TerminologyCorrector.Apply("x", string.Empty, Table()).Text.Should().BeEmpty();
        TerminologyCorrector.Apply(string.Empty, "y", Table()).Text.Should().Be("y");
    }
}
