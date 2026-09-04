using System.Collections.Generic;
using System.Linq;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Terminology;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification.Structure;
using BetterTranslator.Engine.Verification.Terminology;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class TerminologyCheckTests
{
    private static readonly CheckRunSettings EnglishToCzech = new() { SourceLanguage = "en", TargetLanguage = "cs" };

    private static readonly string[] ProvisionSource =
    [
        "The provision applies to every contract.",
        "Read the provision before signing.",
        "This provision is optional.",
        "A provision can be removed later.",
        "The last provision closes the agreement.",
    ];

    private static readonly string[] ProvisionInflected =
    [
        "Ustanovení platí pro každou smlouvu.",
        "Před podpisem si přečtěte ustanovení.",
        "Toto ustanovení je nepovinné.",
        "Ustanovením lze později odstranit.",
        "Poslední ustanovení uzavírá dohodu.",
    ];

    private static readonly string[] ProvisionScattered =
    [
        "Ustanovení platí pro každou smlouvu.",
        "Před podpisem si přečtěte opatření.",
        "Toto ustanovení je nepovinné.",
        "Poskytnutí lze později odstranit.",
        "Poslední zajištění uzavírá dohodu.",
    ];

    private static TableLemmatizer Czech() =>
        new TableLemmatizer("cs")
            .Add("ustanovení", "ustanovením", "ustanoveních", "ustanoveními")
            .Add("opatření", "opatřením", "opatřeních")
            .Add("poskytnutí", "poskytnutím")
            .Add("zajištění", "zajištěním")
            .Add("smlouva", "smlouvu", "smlouvy", "smlouvou")
            .Add("platit", "platí")
            .Add("nepovinný", "nepovinné")
            .Add("odstranit")
            .Add("uzavírat", "uzavírá")
            .Add("dohoda", "dohodu")
            .Add("podpis", "podpisem")
            .Add("přečíst", "přečtěte")
            .Add("poslední")
            .Add("nastavení", "nastaveních", "nastavením")
            .Add("okno", "okna", "okně", "oknem")
            .Add("složka", "složku", "složky", "složce")
            .Add("adresář", "adresáře", "adresáři")
            .Add("otevřít", "otevřete")
            .Add("uložit", "uložte")
            .Add("soubor", "soubory", "souboru");

    private static TerminologyGlossary ProvisionGlossary() =>
        new TerminologyGlossary().Add("provision", "ustanovení", "opatření", "poskytnutí", "zajištění");

    private static (string Source, string Target, IReadOnlyList<SegmentTrace> Traces) Lines(IReadOnlyList<string> source, IReadOnlyList<string> target)
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

    private static CheckContext Context(
        IReadOnlyList<string> source,
        IReadOnlyList<string> target,
        TerminologyServices? services,
        IEnumerable<ExemptSpan>? exemptions = null)
    {
        var (sourceText, targetText, traces) = Lines(source, target);
        var context = StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, exemptions, EnglishToCzech);

        if (services is not null)
        {
            TerminologyPorts.Attach(context, services);
        }

        return context;
    }

    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) =>
        CheckRegistry.Default.Find(checkId)!.Run(context);

    private static int Offset(string text, string word, int occurrence = 0)
    {
        var at = -1;

        for (var i = 0; i <= occurrence; i++)
        {
            at = text.IndexOf(word, at + 1, System.StringComparison.Ordinal);
        }

        return at;
    }

    [Fact]
    public void Three_terminology_checks_register_in_order()
    {
        CheckRegistry.Default.ForCategory(CheckId.Terminology.Category).Select(c => c.CheckId).Should().Equal(
            CheckId.Terminology.AcceptedRendering,
            CheckId.Terminology.RejectedRendering,
            CheckId.Terminology.RunConsistency);
    }

    [Fact]
    public void Actions_are_repair_for_renderings_and_mark_for_consistency()
    {
        var context = Context(ProvisionSource, ProvisionScattered, new TerminologyServices(Czech(), ProvisionGlossary()));

        Run(CheckId.Terminology.RejectedRendering, context).Should().OnlyContain(f => f.Action == CheckAction.Repair && f.Severity == CheckSeverity.Defect);
        Run(CheckId.Terminology.RunConsistency, context).Should().OnlyContain(f => f.Action == CheckAction.Mark && f.Severity == CheckSeverity.Defect);

        var missing = Context(["Open the settings."], ["Otevřete okno."], new TerminologyServices(Czech(), new TerminologyGlossary().Add("settings", "nastavení")));
        Run(CheckId.Terminology.AcceptedRendering, missing).Should().OnlyContain(f => f.Action == CheckAction.Repair && f.Severity == CheckSeverity.Defect);
    }

    [Fact]
    public void Trm101_passes_when_the_accepted_rendering_is_present()
    {
        var glossary = new TerminologyGlossary().Add("settings", "nastavení");
        var context = Context(["Open the settings window."], ["Otevřete okno nastavení."], new TerminologyServices(Czech(), glossary));

        Run(CheckId.Terminology.AcceptedRendering, context).Should().BeEmpty();
    }

    [Fact]
    public void Trm101_fires_when_the_accepted_rendering_is_absent()
    {
        var glossary = new TerminologyGlossary().Add("settings", "nastavení");
        var context = Context(["Open the settings window."], ["Otevřete okno."], new TerminologyServices(Czech(), glossary));

        var findings = Run(CheckId.Terminology.AcceptedRendering, context);

        findings.Should().ContainSingle();
        findings[0].Severity.Should().Be(CheckSeverity.Defect);
        findings[0].Action.Should().Be(CheckAction.Repair);
        findings[0].Evidence.Should().Contain("'nastavení'");
        findings[0].SourceRange.Should().Be(new CheckRange("/0", Offset(string.Join('\n', new[] { "Open the settings window." }), "settings"), "settings".Length));
    }

    [Fact]
    public void Trm102_passes_when_no_rejected_rendering_appears()
    {
        var glossary = new TerminologyGlossary().Add("folder", "složka", "adresář");
        var context = Context(["Save it to the folder."], ["Uložte to do složky."], new TerminologyServices(Czech(), glossary));

        Run(CheckId.Terminology.RejectedRendering, context).Should().BeEmpty();
    }

    [Fact]
    public void Trm102_fires_on_a_rejected_rendering_in_any_inflected_form()
    {
        var glossary = new TerminologyGlossary().Add("folder", "složka", "adresář");
        var target = "Uložte to do adresáře.";
        var context = Context(["Save it to the folder."], [target], new TerminologyServices(Czech(), glossary));

        var findings = Run(CheckId.Terminology.RejectedRendering, context);

        findings.Should().ContainSingle();
        findings[0].Action.Should().Be(CheckAction.Repair);
        findings[0].Severity.Should().Be(CheckSeverity.Defect);
        findings[0].Granularity.Should().Be(CheckGranularity.Word);
        findings[0].TargetRange.Should().Be(new CheckRange("/0", Offset(target, "adresáře"), "adresáře".Length));
        findings[0].Evidence.Should().Contain("'adresář'").And.Contain("'složka'");
    }

    [Fact]
    public void Trm103_raises_nothing_on_four_inflected_forms_of_the_accepted_rendering()
    {
        var context = Context(ProvisionSource, ProvisionInflected, new TerminologyServices(Czech(), ProvisionGlossary()));

        Run(CheckId.Terminology.RunConsistency, context).Should().BeEmpty();
        Run(CheckId.Terminology.AcceptedRendering, context).Should().BeEmpty();
        Run(CheckId.Terminology.RejectedRendering, context).Should().BeEmpty();

        var term = TermIndex.For(context).Find("provision")!;
        term.Occurrences.Should().HaveCount(5);
        term.DistinctLemmas.Should().Equal("ustanovení");
        term.Occurrences.Select(o => o.Rendering).Distinct().Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Trm103_fires_on_the_reported_defect_with_every_occurrence_and_the_majority()
    {
        var context = Context(ProvisionSource, ProvisionScattered, new TerminologyServices(Czech(), ProvisionGlossary()));

        var findings = Run(CheckId.Terminology.RunConsistency, context);

        findings.Should().ContainSingle();
        var finding = findings[0];
        finding.Severity.Should().Be(CheckSeverity.Defect);
        finding.Action.Should().Be(CheckAction.Mark);
        finding.Granularity.Should().Be(CheckGranularity.Document);
        finding.Evidence.Should().Contain("rendered 4 ways across 5 occurrences");
        finding.Evidence.Should().Contain("majority 'Ustanovení' (2/5)");

        var term = TermIndex.For(context).Find("provision")!;
        term.DistinctLemmas.Should().Equal("opatření", "poskytnutí", "ustanovení", "zajištění");
        term.MajorityLemma.Should().Be("ustanovení");

        foreach (var occurrence in term.Occurrences)
        {
            finding.Evidence.Should().Contain($"@{occurrence.TargetRange.Offset}+{occurrence.TargetRange.Length}=");
        }
    }

    [Fact]
    public void Trm103_proposes_a_glossary_entry_without_writing_one()
    {
        var glossary = new TerminologyGlossary();
        var source = new[] { "The provision applies here.", "The rule applies too.", "The provision applies there.", "The provision applies everywhere." };
        var target = new[] { "Ustanovení platí zde.", "Pravidlo platí také.", "Opatření platí tam.", "Zajištění platí všude." };
        var context = Context(source, target, new TerminologyServices(Czech(), glossary));

        var findings = Run(CheckId.Terminology.RunConsistency, context);

        findings.Should().ContainSingle(f => f.Evidence.Contains("'provision'"));
        var finding = findings.Single(f => f.Evidence.Contains("'provision'"));
        finding.Severity.Should().Be(CheckSeverity.Advisory);
        finding.Action.Should().Be(CheckAction.Mark);
        finding.Evidence.Should().Contain("propose glossary entry 'provision' = ");
        glossary.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Checks_skip_when_no_lemmatizer_is_attached()
    {
        var context = Context(ProvisionSource, ProvisionScattered, null);

        Run(CheckId.Terminology.RunConsistency, context).Should().BeEmpty();

        var report = TerminologyRunReport.Build(context);
        report.Skipped.Should().Be(3);
        report.Ran.Should().Be(0);
        report.Checks.Should().OnlyContain(c => !c.CountsTowardPassTotal && c.Reason.Contains(TerminologyServices.NotAttachedReason));
    }

    [Fact]
    public void Checks_skip_when_the_lemmatizer_covers_another_language()
    {
        var services = new TerminologyServices(new TableLemmatizer("sk"), ProvisionGlossary());
        var context = Context(ProvisionSource, ProvisionScattered, services);

        TerminologyRunReport.Build(context).Skipped.Should().Be(3);
    }

    [Fact]
    public void An_exempt_span_produces_no_finding()
    {
        var glossary = new TerminologyGlossary().Add("folder", "složka", "adresář");
        var target = "Uložte to do adresáře.";
        var exemption = new ExemptSpan(new CheckRange("/", Offset(target, "adresáře"), "adresáře".Length), ExemptionReason.SettingsRule, "adresáře");
        var context = Context(["Save it to the folder."], [target], new TerminologyServices(Czech(), glossary), [exemption]);

        Run(CheckId.Terminology.RejectedRendering, context).Should().BeEmpty();
        Run(CheckId.Terminology.AcceptedRendering, context).Should().BeEmpty();
    }

    [Fact]
    public void Findings_are_deterministic_across_runs()
    {
        var first = Context(ProvisionSource, ProvisionScattered, new TerminologyServices(Czech(), ProvisionGlossary()));
        var second = Context(ProvisionSource, ProvisionScattered, new TerminologyServices(Czech(), ProvisionGlossary()));

        var a = CheckRegistry.Default.ForCategory(CheckId.Terminology.Category).SelectMany(c => c.Run(first)).ToList();
        var b = CheckRegistry.Default.ForCategory(CheckId.Terminology.Category).SelectMany(c => c.Run(second)).ToList();

        a.Should().Equal(b);
        a.Should().NotBeEmpty();
    }

    [Fact]
    public void Term_index_is_scoped_to_one_context()
    {
        var services = new TerminologyServices(Czech(), ProvisionGlossary());
        var first = Context(ProvisionSource, ProvisionScattered, services);
        var second = Context(ProvisionSource, ProvisionInflected, services);

        TermIndex.For(first).Find("provision")!.DistinctLemmas.Should().HaveCount(4);
        TermIndex.For(second).Find("provision")!.DistinctLemmas.Should().HaveCount(1);
    }

    [Fact]
    public void Majka_output_yields_the_lemma()
    {
        MajkaLemmatizer.FirstLemma("ustanovením:ustanovení:k1gNnSc7").Should().Be("ustanovení");
        MajkaLemmatizer.FirstLemma("platí:platit:k5eAaImIp3nS:platit:k5eAaImIp3nP").Should().Be("platit");
        MajkaLemmatizer.FirstLemma("xyz").Should().BeNull();
        MajkaLemmatizer.Resolve("cs", null, null).Available.Should().BeFalse();
    }

    [Fact]
    public void Glossary_adapter_layers_project_terms_over_domain_terms()
    {
        var domain = new Engine.Terminology.DomainTermTable([new Engine.Terminology.DomainTerm("folder", "složka", ["adresář"], "")]);
        var project = new Engine.Slop.GlossaryTables([new Engine.Slop.GlossaryTerm("folder", "adresář", "adresář")], [], ["Engine"]);

        var glossary = TerminologyGlossaryAdapter.From(domain, project);

        glossary.Find("folder")!.Accepted.Should().Be("adresář");
        glossary.Find("Engine")!.DoNotTranslate.Should().BeTrue();
    }
}
