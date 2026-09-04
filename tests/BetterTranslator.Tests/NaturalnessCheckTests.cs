using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Naturalness;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public static class NaturalnessFixtures
{
    public static TableTagger CzechTagger() =>
        new TableTagger("cs")
            .Add("uložte", "k5eAaPmRp2nP")
            .Add("otevřete", "k5eAaPmRp2nP")
            .Add("zavřete", "k5eAaPmRp2nP")
            .Add("ukládá", "k5eAaImIp3nS")
            .Add("aktualizuje", "k5eAaImIp3nS")
            .Add("provedeme", "k5eAaPmIp1nP")
            .Add("provede", "k5eAaPmIp3nS")
            .Add("kontrolujeme", "k5eAaImIp1nP")
            .Add("jsem", "k5eAaImIp1nS")
            .Add("byl", "k5eAaImAgMnS")
            .Add("uložen", "k5eAaPmNgMnS")
            .Add("nastavení", "k1gNnSc1")
            .Add("soubor", "k1gInSc1")
            .Add("dokument", "k1gInSc1")
            .Add("index", "k1gInSc1")
            .Add("provedení", "k1gNnSc4")
            .Add("kontrola", "k1gFnSc1")
            .Add("okno", "k1gNnSc4")
            .Add("složky", "k1gFnSc2")
            .Add("změny", "k1gFnPc4")
            .Add("my", "k3xPp1nPc1")
            .Add("já", "k3xPp1nSc1")
            .Add("vy", "k3xPp2nPc1")
            .Add("se", "k3xPyFc4")
            .Add("si", "k3xPyFc3")
            .Add("ho", "k3xPp3gMnSc4")
            .Add("do", "k7c2")
            .Add("a", "k8xC")
            .Add("je", "k5eAaImIp3nS")
            .Add("překlad", "k1gInSc1")
            .Add("hotov", "k2eAgMnSc1d1");

    public static TableTagger SlovakTagger() =>
        new TableTagger("sk")
            .Add("uložte", "k5eAaPmRp2nP")
            .Add("otvorte", "k5eAaPmRp2nP")
            .Add("ukladá", "k5eAaImIp3nS")
            .Add("súbor", "k1gInSc1")
            .Add("priečinka", "k1gInSc2")
            .Add("okno", "k1gNnSc4")
            .Add("nastavení", "k1gNnPc2")
            .Add("dokument", "k1gInSc1")
            .Add("sa", "k3xPyFc4")
            .Add("si", "k3xPyFc3")
            .Add("som", "k5eAaImIp1nS")
            .Add("by", "k9")
            .Add("rád", "k2eAgMnSc1d1")
            .Add("do", "k7c2");

    public static (string Source, string Target, IReadOnlyList<SegmentTrace> Traces) Lines(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            traces.Add(new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Translated, target[i], null, target[i], targetAt, target[i].Length));
            sourceAt += source[i].Length + 1;
            targetAt += target[i].Length + 1;
        }

        return (string.Join('\n', source), string.Join('\n', target), traces);
    }

    public static CheckContext Context(IReadOnlyList<string> source, IReadOnlyList<string> target, NaturalnessServices services, string targetLanguage = "cs", IEnumerable<ExemptSpan>? exemptions = null)
    {
        var (sourceText, targetText, traces) = Lines(source, target);
        var context = StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, exemptions, new CheckRunSettings { SourceLanguage = "en", TargetLanguage = targetLanguage });
        NaturalnessPorts.Attach(context, services);
        return context;
    }

    public static NaturalnessServices Czech(NaturalnessProfile? profile = null) => NaturalnessServices.Shipped(CzechTagger(), profile);

    public static NaturalnessServices Slovak(NaturalnessProfile? profile = null) => NaturalnessServices.Shipped(SlovakTagger(), profile);

    public static NaturalnessProfile CalibratedForTests() =>
        new()
        {
            Source = "test",
            ImprovementMargin = 1,
            Pairs =
            [
                new NaturalnessPairProfile("en-cs", NaturalnessProfile.UnitSentence, new NaturalnessBand(0.2, 40, 4), new NaturalnessBand(0.5, 0, 6), new NaturalnessBand(0, 40, 1), new Dictionary<string, int>()),
                new NaturalnessPairProfile("en-cs", NaturalnessProfile.UnitBlock, new NaturalnessBand(0.2, 10, 4), new NaturalnessBand(0.5, 0, 6), new NaturalnessBand(0, 10, 1), new Dictionary<string, int>()),
                new NaturalnessPairProfile("en-sk", NaturalnessProfile.UnitSentence, new NaturalnessBand(0.2, 10, 4), new NaturalnessBand(0.5, 0, 6), new NaturalnessBand(0, 10, 1), new Dictionary<string, int>()),
            ],
        };
}

public sealed class NaturalnessCheckTests
{
    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) => CheckRegistry.Default.Find(checkId)!.Run(context);

    [Fact]
    public void Ten_naturalness_checks_register_in_order()
    {
        CheckRegistry.Default.ForCategory(CheckId.Naturalness.Category).Select(c => c.CheckId).Should().Equal(
            CheckId.Naturalness.AlignmentCrossing,
            CheckId.Naturalness.TagSequenceDivergence,
            CheckId.Naturalness.CliticPlacement,
            CheckId.Naturalness.PronounExplicitness,
            CheckId.Naturalness.NominalStyle,
            CheckId.Naturalness.PassiveCalque,
            CheckId.Naturalness.RegisterConsistency,
            CheckId.Naturalness.SentenceBoundaryFit,
            CheckId.Naturalness.TitleCaseAndQuotation,
            CheckId.Naturalness.PluralCategoryCoverage);
    }

    [Fact]
    public void Calqued_english_order_fires_alignment_crossing()
    {
        var source = new[] { "Version 2.3 of BetterTranslator exports index 7 and index 9 to folder Alpha and folder Beta." };
        var target = new[] { "Verze 2.3 BetterTranslator exportuje index 7 a index 9 do složky Alpha a složky Beta." };
        var context = NaturalnessFixtures.Context(source, target, NaturalnessFixtures.Czech(NaturalnessFixtures.CalibratedForTests()));

        var findings = Run(CheckId.Naturalness.AlignmentCrossing, context);

        findings.Should().ContainSingle();
        findings[0].Action.Should().Be(CheckAction.Rewrite);
        findings[0].Severity.Should().Be(CheckSeverity.Score);
        findings[0].Evidence.Should().Contain("0 crossings");
    }

    [Fact]
    public void Clitic_in_first_position_fires()
    {
        var context = NaturalnessFixtures.Context(["The file is saved."], ["Se soubor ukládá."], NaturalnessFixtures.Czech());

        var findings = Run(CheckId.Naturalness.CliticPlacement, context);

        findings.Should().ContainSingle(f => f.Evidence.Contains("first position"));
        findings[0].Granularity.Should().Be(CheckGranularity.Word);
        findings[0].TargetRange.Offset.Should().Be(0);
    }

    [Fact]
    public void Clitic_cluster_in_wrong_internal_order_fires()
    {
        var context = NaturalnessFixtures.Context(["I would have saved it."], ["Rád se bych ho uložil."], NaturalnessFixtures.Czech());

        var findings = Run(CheckId.Naturalness.CliticPlacement, context);

        findings.Should().ContainSingle(f => f.Evidence.Contains("out of cluster order"));
        findings[0].Evidence.Should().Contain("'bych'");
    }

    [Fact]
    public void Explicit_subject_pronoun_without_contrast_fires()
    {
        var context = NaturalnessFixtures.Context(["We check the document."], ["My kontrolujeme dokument."], NaturalnessFixtures.Czech());

        var findings = Run(CheckId.Naturalness.PronounExplicitness, context);

        findings.Should().ContainSingle();
        findings[0].Evidence.Should().Contain("'My'");
        findings[0].Action.Should().Be(CheckAction.Rewrite);
    }

    [Fact]
    public void Pronoun_rule_downgrades_to_judgment_without_an_analyzer()
    {
        var services = new NaturalnessServices(NaturalnessPackLoader.ShippedPacks, new UnavailableTagger("cs", "not installed"));
        var context = NaturalnessFixtures.Context(["We check the document."], ["My kontrolujeme dokument."], services);

        Run(CheckId.Naturalness.PronounExplicitness, context).Should().BeEmpty();
        NaturalnessEvidenceStore.For(context).Items.Should().BeEmpty("without tags the pronoun rule cannot see a verb, so it collects nothing rather than guessing");
        services.LoadNotice("cs").Should().Contain("model judgment");
    }

    [Fact]
    public void Title_cased_heading_fires()
    {
        var source = "# Open The Settings Window\n\nSave the file.";
        var target = "# Otevřete Okno Nastavení Složky\n\nUložte soubor.";
        var context = StructureContext.Build(source, target, settings: new CheckRunSettings { SourceLanguage = "en", TargetLanguage = "cs" });
        NaturalnessPorts.Attach(context, NaturalnessFixtures.Czech());

        var findings = Run(CheckId.Naturalness.TitleCaseAndQuotation, context);

        findings.Should().Contain(f => f.Evidence.Contains("title case heading"));
    }

    [Fact]
    public void English_quotation_pair_fires_and_czech_pair_does_not()
    {
        var english = NaturalnessFixtures.Context(["Press \"Save\" now."], ["Stiskněte \"Uložit\" nyní."], NaturalnessFixtures.Czech());
        var czech = NaturalnessFixtures.Context(["Press \"Save\" now."], ["Stiskněte „Uložit“ nyní."], NaturalnessFixtures.Czech());

        Run(CheckId.Naturalness.TitleCaseAndQuotation, english).Should().ContainSingle(f => f.Evidence.Contains("quotation pair"));
        Run(CheckId.Naturalness.TitleCaseAndQuotation, czech).Should().BeEmpty();
    }

    [Fact]
    public void A_natural_czech_sentence_fires_nothing()
    {
        var source = new[] { "Open the settings window and save the changes.", "The document is saved and the index updates." };
        var target = new[] { "Otevřete okno nastavení a uložte změny.", "Dokument se ukládá a index se aktualizuje." };
        var context = NaturalnessFixtures.Context(source, target, NaturalnessFixtures.Czech(NaturalnessFixtures.CalibratedForTests()));

        var findings = CheckRegistry.Default.ForCategory(CheckId.Naturalness.Category).SelectMany(c => c.Run(context)).ToList();

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Judgment_rules_collect_evidence_instead_of_findings()
    {
        var context = NaturalnessFixtures.Context(["We perform a check of the file."], ["Provedeme kontrolu souboru a soubor byl uložen."], NaturalnessFixtures.Czech());

        Run(CheckId.Naturalness.PassiveCalque, context).Should().BeEmpty();

        var evidence = NaturalnessEvidenceStore.For(context).Items;
        evidence.Should().Contain(e => e.CheckId == CheckId.Naturalness.PassiveCalque && e.Evidence.Contains("byl uložen"));
    }

    [Fact]
    public void An_untranslated_sentence_is_never_examined()
    {
        var context = NaturalnessFixtures.Context(["The file is saved."], ["The file is saved."], NaturalnessFixtures.Czech());

        NaturalnessCheck.Sentences(context).Should().BeEmpty();
    }

    [Fact]
    public void An_exempt_span_is_never_examined()
    {
        var target = "Se soubor ukládá.";
        var exemption = new ExemptSpan(new CheckRange("/0", 0, 2), ExemptionReason.ProtectedName, "Se");
        var context = NaturalnessFixtures.Context(["The file is saved."], [target], NaturalnessFixtures.Czech(), exemptions: [exemption]);

        Run(CheckId.Naturalness.CliticPlacement, context).Should().BeEmpty();
    }

    [Fact]
    public void Checks_skip_for_a_language_without_a_pack()
    {
        var context = NaturalnessFixtures.Context(["The file is saved."], ["Die Datei wird gespeichert."], NaturalnessFixtures.Czech(), targetLanguage: "de");

        CheckRegistry.Default.ForCategory(CheckId.Naturalness.Category).SelectMany(c => c.Run(context)).Should().BeEmpty();
        NaturalnessFixtures.Czech().SkipReason(context.Settings).Should().Contain("coverage matrix");
    }

    [Fact]
    public void Plural_coverage_is_advisory_and_only_on_resource_strings()
    {
        var source = "{\n  \"count\": \"{0} item(s)\"\n}";
        var target = "{\n  \"count\": \"{0} položek\"\n}";
        var context = StructureContext.Build(source, target, settings: new CheckRunSettings { SourceLanguage = "en", TargetLanguage = "cs" });
        NaturalnessPorts.Attach(context, NaturalnessFixtures.Czech());

        var findings = Run(CheckId.Naturalness.PluralCategoryCoverage, context);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(CheckSeverity.Advisory);
        findings[0].Action.Should().Be(CheckAction.Mark);

        var prose = NaturalnessFixtures.Context(["{0} item(s)"], ["{0} položek"], NaturalnessFixtures.Czech());
        Run(CheckId.Naturalness.PluralCategoryCoverage, prose).Should().BeEmpty();
    }

    [Fact]
    public void Routing_carries_rewrite_for_every_score_check_and_never_repair()
    {
        var table = BetterTranslator.Core.Verification.Gate.RoutingTable.Default;

        foreach (var id in new[] { "NAT-101", "NAT-102", "NAT-103", "NAT-104", "NAT-105", "NAT-106", "NAT-107", "NAT-108", "NAT-109" })
        {
            table.ActionFor(new CheckFinding(id, new CheckRange("/", 0, 1), null, CheckGranularity.Sentence, CheckSeverity.Score, 70, CheckCause.ModelOutput, "x", CheckAction.Mark)).Should().Be(CheckAction.Rewrite, id);
        }

        table.ActionFor(new CheckFinding("NAT-110", new CheckRange("/", 0, 1), null, CheckGranularity.Sentence, CheckSeverity.Advisory, 60, CheckCause.ModelOutput, "x", CheckAction.Mark)).Should().Be(CheckAction.Mark);
        table.Rules.Where(r => r.Category == "NAT").Should().NotContain(r => r.Action == CheckAction.Repair);
        BetterTranslator.Core.Verification.Gate.StageTable.Default.Categories.Should().Contain("NAT");
    }
}

public sealed class SlovakPackTests
{
    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) => CheckRegistry.Default.Find(checkId)!.Run(context);

    [Fact]
    public void Slovak_pack_carries_its_own_inventory()
    {
        var pack = NaturalnessPackLoader.ShippedPacks["sk"].Pack!;
        var czech = NaturalnessPackLoader.ShippedPacks["cs"].Pack!;

        pack.AllClitics.Should().Contain(["sa", "som", "ju", "ich"]);
        pack.AllClitics.Should().NotContain(["bych", "jsem", "mě"]);
        pack.TwoWordConditional.Should().NotBeNull();
        pack.TwoWordConditional!.Auxiliaries.Should().Equal("som", "si", "sme", "ste");
        pack.LightVerbs.Should().NotIntersectWith(czech.LightVerbs);
        pack.CliticGroups.Select(g => g.Name).Should().Equal(czech.CliticGroups.Select(g => g.Name), "the cluster order is the same shape with different forms");
    }

    [Fact]
    public void Slovak_clitic_in_first_position_fires()
    {
        var context = NaturalnessFixtures.Context(["The file is saved."], ["Sa súbor ukladá."], NaturalnessFixtures.Slovak(), targetLanguage: "sk");

        Run(CheckId.Naturalness.CliticPlacement, context).Should().ContainSingle(f => f.Evidence.Contains("'Sa'") && f.Evidence.Contains("first position"));
    }

    [Fact]
    public void Contracted_conditional_fires_in_slovak()
    {
        var context = NaturalnessFixtures.Context(["I would save the file."], ["Rád bych uložil súbor."], NaturalnessFixtures.Slovak(), targetLanguage: "sk");

        Run(CheckId.Naturalness.CliticPlacement, context).Should().ContainSingle(f => f.Evidence.Contains("'bych'") && f.Evidence.Contains("two words"));
    }

    [Fact]
    public void A_natural_slovak_sentence_fires_nothing()
    {
        var source = new[] { "Open the settings window and save the file." };
        var target = new[] { "Otvorte okno nastavení a uložte súbor." };
        var context = NaturalnessFixtures.Context(source, target, NaturalnessFixtures.Slovak(NaturalnessFixtures.CalibratedForTests()), targetLanguage: "sk");

        CheckRegistry.Default.ForCategory(CheckId.Naturalness.Category).SelectMany(c => c.Run(context)).Should().BeEmpty();
    }

    [Fact]
    public void A_czech_form_in_the_slovak_pack_fails_validation()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(NaturalnessPackLoader.ShippedPacks["sk"].Pack).Replace("\"sa\"", "\"jsem\"", System.StringComparison.Ordinal);

        var result = NaturalnessPackLoader.Parse("sk", json);

        result.Loaded.Should().BeFalse();
        result.Reason.Should().Contain("jsem").And.Contain("another language");
    }

    [Fact]
    public void A_letter_outside_the_alphabet_fails_validation()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(NaturalnessPackLoader.ShippedPacks["sk"].Pack).Replace("\"ma\"", "\"mě\"", System.StringComparison.Ordinal);

        NaturalnessPackLoader.Parse("sk", json).Loaded.Should().BeFalse();
    }
}

public sealed class PackSchemaTests
{
    private const string GermanFixture = """
        {
          "language": "de",
          "name": "German",
          "analyzer": "none",
          "proDrop": false,
          "alphabet": "aäbcdefghijklmnoöpqrsßtuüvwxyz",
          "foreignForms": [],
          "cliticGroups": [],
          "clauseBreakers": [",", ";", "dass", "weil", "wenn"],
          "subjectPronouns": ["ich", "du", "er", "sie", "es", "wir", "ihr"],
          "quotationOpen": "„",
          "quotationClose": "“",
          "foreignQuotationPairs": [["“", "”"]],
          "tags": { "verb": "V", "person": "p" },
          "extensions": { "verbSecond": { "group": "4", "finiteVerbSlot": 2, "subordinateFinal": true, "subordinators": ["dass", "weil", "wenn", "ob"] } },
          "rules": [
            { "id": "NAT-104", "signal": "inventory+tags", "severity": "Score", "decidable": true, "evidence": "explicit subject pronoun '{form}'" },
            { "id": "NAT-109", "signal": "inventory", "severity": "Score", "decidable": true, "evidence": "{kind}: '{form}'" }
          ]
        }
        """;

    [Fact]
    public void A_german_pack_fixture_loads_without_a_schema_change()
    {
        var result = NaturalnessPackLoader.Parse("de", GermanFixture);

        result.Loaded.Should().BeTrue(result.Reason);
        result.Pack!.Extensions.Should().ContainKey("verbSecond");
        result.Pack.Extensions["verbSecond"].GetProperty("subordinateFinal").GetBoolean().Should().BeTrue();
        result.Pack.ProDrop.Should().BeFalse();
    }

    [Fact]
    public void A_pack_that_is_not_pro_drop_never_inherits_the_pro_drop_rule()
    {
        var packs = new Dictionary<string, PackLoadResult>(NaturalnessPackLoader.ShippedPacks, System.StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = NaturalnessPackLoader.Parse("de", GermanFixture),
        };
        var services = new NaturalnessServices(packs, new TableTagger("de").Add("speichern", "Vp1"));
        var context = NaturalnessFixtures.Context(["We save the file."], ["Wir speichern die Datei."], services, targetLanguage: "de");

        CheckRegistry.Default.Find(CheckId.Naturalness.PronounExplicitness)!.Run(context).Should().BeEmpty();
    }

    [Fact]
    public void Shipped_packs_are_only_czech_and_slovak_and_both_load()
    {
        NaturalnessPackLoader.ShippedPacks.Keys.Should().BeEquivalentTo(["cs", "sk"]);
        NaturalnessPackLoader.ShippedPacks.Values.Should().OnlyContain(p => p.Loaded);
    }

    [Fact]
    public void A_malformed_pack_disables_its_language_with_a_reason()
    {
        NaturalnessPackLoader.Parse("xx", "{ not json").Loaded.Should().BeFalse();
        NaturalnessPackLoader.Parse("xx", "{ \"language\": \"xx\", \"rules\": [] }").Reason.Should().Contain("no rules");
    }
}

public sealed class CoverageMatrixTests
{
    [Fact]
    public void The_matrix_covers_every_language_the_app_offers_and_nothing_else()
    {
        var registry = new BetterTranslator.Engine.Languages.LanguageRegistry();
        var offered = registry.All.Select(l => l.Code).ToList();
        var matrix = CoverageMatrix.Default;

        matrix.Validate().Should().BeNull();
        matrix.Languages.Select(l => l.Code).Should().BeEquivalentTo(offered);

        var eurollm = registry.ForModel("eurollm").Select(l => l.Code).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        foreach (var entry in matrix.Languages)
        {
            entry.Models.Contains("eurollm").Should().Be(eurollm.Contains(entry.Code), entry.Code);
            entry.Models.Should().Contain("translategemma");
        }

        matrix.Languages.Where(l => l.Implemented).Select(l => l.Code).Should().BeEquivalentTo(["cs", "sk"]);
        matrix.Languages.Where(l => !l.Implemented && !l.Unclassified).Should().OnlyContain(l => l.Status == CoverageMatrix.StatusDeclared);
        matrix.Languages.Where(l => l.Unclassified).Should().OnlyContain(l => l.Group == null && l.Mechanism != null);
        matrix.Languages.Where(l => l.Status == CoverageMatrix.StatusDeclared).Should().OnlyContain(l => NaturalnessPackLoader.ShippedPacks.ContainsKey(l.Code) == false);
    }
}
