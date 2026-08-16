using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The order the gates run in, which is not arbitrary.
///
/// A translator's note is stripped first, so a good answer is not thrown away
/// over a trailing annotation. The leak gate runs before the repairs, because an
/// answer that narrates the task is not worth repairing. The slop validator then
/// repairs what it can, and the structural and glossary gates judge the repaired
/// text -- which is what the document would actually receive.
/// </summary>
[Collection(EngineConfigCollection.Name)]
public sealed class GuardPipelineTests
{
    private static readonly RagConfig Czech = RagConfig.For("cs");

    private static readonly DoNotTranslateLists Terms = DoNotTranslate.Load();

    private const double MaxRatio = 2.4;

    private const double MinRatio = 0.35;

    /// <summary>The same sequence LocalTranslator runs, without a model.</summary>
    private static string? Refuse(string source, string? answer, string prompt = "")
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return "no answer";
        }

        var cleaned = ChunkIntegrity.RemoveTranslatorNote(source, answer);

        var leak = OutputLeak.Check(source, cleaned, prompt, MaxRatio);
        if (leak is not null)
        {
            return "leak: " + leak;
        }

        var slop = SlopValidator.Validate(source, cleaned, Czech.Slop);
        if (slop.Verdict == SlopVerdict.Integrity)
        {
            return "placeholder damage";
        }

        var integrity = ChunkIntegrity.Check(source, slop.Text, Terms.Strict, Terms.Declinable, MinRatio);
        if (integrity is not null)
        {
            return integrity;
        }

        var glossary = Glossary.Check(Czech.Glossary, source, slop.Text);
        if (glossary is not null)
        {
            return glossary;
        }

        return string.Equals(slop.Text.Trim(), source.Trim(), StringComparison.Ordinal)
            && TranslationCandidate.IsWorthSending(source, Terms.Strict)
                ? "unchanged"
                : null;
    }

    [Fact]
    public void ACleanTranslationPassesEveryGate()
    {
        Refuse("The build failed.", "Sestavení selhalo.").Should().BeNull();
    }

    [Fact]
    public void ATrailingTranslatorNoteIsStrippedRatherThanRefused()
    {
        // It used to be caught by the leak gate and the whole line thrown away
        // over an annotation, which is why the strip runs first.
        Refuse("The build failed.", "Sestavení selhalo. (přeloženo z angličtiny)").Should().BeNull();
    }

    [Fact]
    public void NarrationIsRefusedBeforeAnythingTriesToRepairIt()
    {
        Refuse("The build failed.", "To translate this text I will follow the rules.")
            .Should().StartWith("leak:");
    }

    [Fact]
    public void PlaceholderDamageIsRefused()
    {
        Refuse("Welcome :user", "Vítejte :uzivatel").Should().Be("placeholder damage");
    }

    [Fact]
    public void AnUnchangedAnswerIsRefusedByWhicheverGateReachesItFirst()
    {
        // Two layers, and the second is why the first is not enough.
        // ChunkIntegrity refuses an echo only from 40 characters up, because
        // below that a short line can legitimately come back looking similar.
        const string Long = "A sentence that is long enough to matter here.";

        Refuse(Long, Long).Should().Be("source returned unchanged");

        // Under that floor nothing structural can tell: the pipeline's own
        // identity check is the only thing left, and an answer identical to its
        // source was not translated whatever else is true of it.
        Refuse("Save changes", "Save changes").Should().Be("unchanged");
    }

    [Fact]
    public void AnEchoIsOnlyADefectWhereSomethingHadToChange()
    {
        // "OK" is "OK" in Czech, and so are Linux, PDF and a table separator.
        // Refusing them costs a retry and then reports a failure for the right
        // answer, so the echo check asks the same question that decides whether
        // a line is worth sending at all.
        Refuse("OK", "OK").Should().BeNull();
        Refuse("Linux", "Linux").Should().BeNull();
        Refuse("|---|---|", "|---|---|").Should().BeNull();
        Refuse("JSON, XML, CSV", "JSON, XML, CSV").Should().BeNull();

        // Long enough to carry meaning, so an identical answer is still refused.
        Refuse("Save changes", "Save changes").Should().Be("unchanged");
    }

    [Fact]
    public void AnEmptyAnswerIsRefused()
    {
        Refuse("The build failed.", "").Should().Be("no answer");
        Refuse("The build failed.", null).Should().Be("no answer");
    }

    [Fact]
    public void SymbolRepairsSurviveTheGatesRatherThanTrippingThem()
    {
        // The em dash is rewritten by tier 1 and the repaired text is what the
        // later gates judge, so a repair must not then be refused for the change
        // it just made.
        Refuse("Cost - about 5 GB", "Cena — asi 5 GB").Should().BeNull();
    }

    [Fact]
    public void AStrictTermLostIsRefusedAndADeclinableOneInflectedIsNot()
    {
        Refuse("The JSON payload is here", "Datová zpráva je zde")
            .Should().StartWith("do-not-translate term lost");

        Refuse("a future PowerShell release", "budoucí vydání PowerShellu").Should().BeNull();
    }
}
