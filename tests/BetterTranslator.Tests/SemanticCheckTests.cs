using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Verification.Structure;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class ConceptEmbeddingBackend : IEmbeddingBackend
{
    private const int Dimension = 8;

    private readonly UnigramTokenizer _tokenizer;

    private readonly IReadOnlyDictionary<string, string> _concepts;

    public ConceptEmbeddingBackend(UnigramTokenizer tokenizer, IReadOnlyDictionary<string, string> concepts)
    {
        _tokenizer = tokenizer;
        _concepts = concepts;
    }

    public string ModelIdentity => "concept-test-model";

    public int Calls { get; private set; }

    public float[,] HiddenStates(IReadOnlyList<int> ids)
    {
        Calls++;
        var hidden = new float[ids.Count, Dimension];

        for (var t = 0; t < ids.Count; t++)
        {
            if (ids[t] == _tokenizer.BeginId || ids[t] == _tokenizer.EndId)
            {
                continue;
            }

            var piece = SemanticFixtures.PieceOf(ids[t]);
            var concept = _concepts.TryGetValue(piece, out var mapped) ? mapped : piece;
            var vector = SemanticFixtures.UnitVector(concept, Dimension);

            for (var d = 0; d < Dimension; d++)
            {
                hidden[t, d] = vector[d];
            }
        }

        return hidden;
    }

    public void Dispose()
    {
    }
}

public sealed class DictionaryReverseTranslator(IReadOnlyDictionary<string, string> answers) : IReverseTranslator
{
    public string ModelIdentity => "reverse-test-model";

    public bool Available => true;

    public string UnavailableReason => string.Empty;

    public int Calls { get; private set; }

    public List<(string Text, string From, string To)> Requests { get; } = [];

    public string? Translate(string text, string fromLanguage, string toLanguage)
    {
        Calls++;
        Requests.Add((text, fromLanguage, toLanguage));
        return answers.TryGetValue(text, out var answer) ? answer : null;
    }
}

public static class SemanticFixtures
{
    public static readonly CheckRunSettings EnglishToCzech = new() { SourceLanguage = "en", TargetLanguage = "cs" };

    public static readonly string[] Pieces =
    [
        "<s>", "<pad>", "</s>", "<unk>",
        "▁the", "▁file", "▁is", "▁on", "▁disk", "▁soubor", "▁je", "▁na", "▁disku", "▁pilník", "▁tool", "▁a", "▁save", "▁uložit", "▁změny", "▁changes", ".", "▁",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "í", "ě", "ž", "ů",
    ];

    public static readonly IReadOnlyDictionary<string, string> Concepts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["▁file"] = "file",
        ["▁soubor"] = "file",
        ["▁disk"] = "disk",
        ["▁disku"] = "disk",
        ["▁is"] = "is",
        ["▁je"] = "is",
        ["▁on"] = "on",
        ["▁na"] = "on",
        ["▁pilník"] = "tool",
        ["▁tool"] = "tool",
        ["▁save"] = "save",
        ["▁uložit"] = "save",
        ["▁changes"] = "changes",
        ["▁změny"] = "changes",
    };

    public static string TokenizerJson()
    {
        var vocab = string.Join(",", Pieces.Select((p, i) => $"[\"{p}\", {(i < 4 ? 0 : p.Length > 1 ? -2 : -9)}]"));
        return "{\"added_tokens\":[{\"id\":0,\"content\":\"<s>\"},{\"id\":2,\"content\":\"</s>\"}],\"model\":{\"type\":\"Unigram\",\"unk_id\":3,\"vocab\":[" + vocab + "]}}";
    }

    public static string PieceOf(int id) => id >= 0 && id < Pieces.Length ? Pieces[id] : "<unk>";

    public static float[] UnitVector(string concept, int dimension)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(concept));
        var vector = new float[dimension];

        for (var d = 0; d < dimension; d++)
        {
            vector[d] = bytes[d] / 255f - 0.5f;
        }

        return EmbeddingMath.Normalize(vector);
    }

    public static (SemanticServices Services, ConceptEmbeddingBackend Backend, DictionaryReverseTranslator Reverse) Services(int cap = 24)
    {
        var tokenizer = UnigramTokenizer.Parse(TokenizerJson());
        var backend = new ConceptEmbeddingBackend(tokenizer, Concepts);
        var host = new EmbeddingHost(() => backend, () => tokenizer, backend.ModelIdentity);
        var reverse = new DictionaryReverseTranslator(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Soubor je na disku."] = "The file is on disk.",
            ["Pilník je na disku."] = "The tool is on disk.",
            ["Uložit změny."] = "Save changes.",
        });

        return (new SemanticServices(host, reverse, new SemanticSettings(cap, 0.6)), backend, reverse);
    }

    public static CheckContext Context(IReadOnlyList<string> source, IReadOnlyList<string> target, IEnumerable<ExemptSpan>? exemptions = null)
    {
        var sourceText = string.Join('\n', source);
        var targetText = string.Join('\n', target);
        var traces = new List<SegmentTrace>();
        var sourceAt = 0;
        var targetAt = 0;

        for (var i = 0; i < source.Count; i++)
        {
            traces.Add(new SegmentTrace(sourceAt, source[i].Length, SegmentOutcome.Translated, target[i], null, target[i], targetAt, target[i].Length));
            sourceAt += source[i].Length + 1;
            targetAt += target[i].Length + 1;
        }

        return StructureContext.Build(sourceText, targetText, ProseStructure.Instance, traces, exemptions, EnglishToCzech);
    }

    public static CheckFinding Flag(CheckContext context, int line, string checkId = CheckId.Coverage.SourceTokenSurvival, int confidence = 80)
    {
        var segment = context.Alignment[line];
        return new CheckFinding(checkId, segment.TargetRange!, segment.SourceRange, CheckGranularity.Sentence, CheckSeverity.Defect, confidence, CheckCause.ModelOutput, "flagged by fixture", CheckAction.Mark);
    }
}

public sealed class SemanticCheckTests
{
    private static readonly string[] Source = ["The file is on disk.", "Save changes."];

    private static readonly string[] Equal = ["Soubor je na disku.", "Uložit změny."];

    private static readonly string[] Different = ["Pilník je na disku.", "Uložit změny."];

    private static IReadOnlyList<CheckFinding> Run(string checkId, CheckContext context) =>
        CheckRegistry.Default.Find(checkId)!.Run(context);

    private static CheckContext Admitted(string[] target, SemanticServices services, params int[] lines)
    {
        var context = SemanticFixtures.Context(Source, target);
        SemanticPorts.Attach(context, services);
        EscalationGate.Admit(context, [.. lines.Select(l => SemanticFixtures.Flag(context, l))], services.Settings.SpanCap);
        return context;
    }

    [Fact]
    public void Three_semantic_checks_register_in_order()
    {
        CheckRegistry.Default.ForCategory(CheckId.Semantics.Category).Select(c => c.CheckId).Should().Equal(
            CheckId.Semantics.EmbeddingSimilarity,
            CheckId.Semantics.ReverseTranslation,
            CheckId.Semantics.ReverseComparison);
    }

    [Fact]
    public void The_tokenizer_segments_with_the_unigram_vocabulary()
    {
        var tokenizer = UnigramTokenizer.Parse(SemanticFixtures.TokenizerJson());
        var encoded = tokenizer.Encode("The file is on disk.");

        encoded.Pieces.Should().Equal("<s>", "▁", "T", "h", "e", "▁file", "▁is", "▁on", "▁disk", ".", "</s>");
        encoded.Ids[0].Should().Be(tokenizer.BeginId);
        encoded.Ids[^1].Should().Be(tokenizer.EndId);
        tokenizer.Encode("soubor").Pieces.Should().Equal("<s>", "▁soubor", "</s>");
    }

    [Fact]
    public void The_escalation_gate_refuses_a_span_no_other_check_flagged()
    {
        var (services, backend, reverse) = SemanticFixtures.Services();
        var unflagged = SemanticFixtures.Context(Source, Different);
        SemanticPorts.Attach(unflagged, services);

        foreach (var check in CheckRegistry.Default.ForCategory(CheckId.Semantics.Category))
        {
            check.Run(unflagged).Should().BeEmpty();
        }

        backend.Calls.Should().Be(0);
        reverse.Calls.Should().Be(0);

        var structural = SemanticFixtures.Context(Source, Different);
        SemanticPorts.Attach(structural, services);
        var decision = EscalationGate.Admit(structural, [SemanticFixtures.Flag(structural, 0, CheckId.Structure.ChunkParity)], services.Settings.SpanCap);

        decision.Admitted.Should().BeEmpty();
        Run(CheckId.Semantics.EmbeddingSimilarity, structural).Should().BeEmpty();
        backend.Calls.Should().Be(0);
    }

    [Fact]
    public void The_per_run_cap_holds()
    {
        var (services, backend, _) = SemanticFixtures.Services(cap: 1);
        var context = SemanticFixtures.Context(Source, Different);
        SemanticPorts.Attach(context, services);

        var decision = EscalationGate.Admit(context, [SemanticFixtures.Flag(context, 0, confidence: 60), SemanticFixtures.Flag(context, 1, confidence: 90)], services.Settings.SpanCap);

        decision.Flagged.Should().Be(2);
        decision.Admitted.Should().ContainSingle().Which.OriginatingConfidence.Should().Be(90);
        decision.Refused.Should().Be(1);
        decision.Cap.Should().Be(1);

        var findings = Run(CheckId.Semantics.EmbeddingSimilarity, context);
        findings.Should().ContainSingle().Which.TargetRange.Offset.Should().Be(Different[0].Length + 1);
        backend.Calls.Should().Be(2);
    }

    [Fact]
    public void Embedding_similarity_scores_an_equal_pair_higher_than_a_different_one()
    {
        var (equalServices, _, _) = SemanticFixtures.Services();
        var (differentServices, _, _) = SemanticFixtures.Services();

        var equal = Run(CheckId.Semantics.EmbeddingSimilarity, Admitted(Equal, equalServices, 0)).Should().ContainSingle().Subject;
        var different = Run(CheckId.Semantics.EmbeddingSimilarity, Admitted(Different, differentServices, 0)).Should().ContainSingle().Subject;

        equal.Confidence.Should().BeLessThan(different.Confidence);
        equal.Evidence.Should().Contain("cross-lingual similarity").And.Contain("escalated by COV-104 at confidence 80");
        new[] { equal, different }.Should().OnlyContain(f => f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly && f.SourceRange != null);
        equal.TargetRange.Should().Be(new CheckRange("/0", 0, Equal[0].Length));
    }

    [Fact]
    public void Reverse_translation_uses_the_swapped_direction_and_scores_overlap()
    {
        var (services, _, reverse) = SemanticFixtures.Services();
        var equal = Run(CheckId.Semantics.ReverseTranslation, Admitted(Equal, services, 0)).Should().ContainSingle().Subject;

        reverse.Requests.Should().ContainSingle().Which.Should().Be(("Soubor je na disku.", "cs", "en"));
        equal.Confidence.Should().Be(0);
        equal.Evidence.Should().Contain("reverse translation 'The file is on disk.'");

        var (other, _, _) = SemanticFixtures.Services();
        var different = Run(CheckId.Semantics.ReverseTranslation, Admitted(Different, other, 0)).Should().ContainSingle().Subject;

        different.Confidence.Should().BeGreaterThan(0);
        different.Evidence.Should().Contain("content token overlap");
        different.Severity.Should().Be(CheckSeverity.Score);
        different.Action.Should().Be(CheckAction.ScoreOnly);
    }

    [Fact]
    public void Reverse_comparison_combines_similarity_and_overlap_and_records_both()
    {
        var (services, _, _) = SemanticFixtures.Services();
        var equal = Run(CheckId.Semantics.ReverseComparison, Admitted(Equal, services, 0)).Should().ContainSingle().Subject;

        var (other, _, _) = SemanticFixtures.Services();
        var different = Run(CheckId.Semantics.ReverseComparison, Admitted(Different, other, 0)).Should().ContainSingle().Subject;

        equal.Confidence.Should().BeLessThan(different.Confidence);
        equal.Evidence.Should().Contain("reverse similarity").And.Contain("content token overlap").And.Contain("combined confidence").And.Contain("carried by");
        ReverseComparisonCheck.Combine(1, 1).Should().Be(0);
        ReverseComparisonCheck.Combine(0, 0).Should().Be(100);
        ReverseComparisonCheck.Combine(0.5, 1).Should().Be(25);
    }

    [Fact]
    public void The_cache_prevents_a_second_embedding_call_and_a_second_reverse_translation()
    {
        var (services, backend, reverse) = SemanticFixtures.Services();
        var context = Admitted(Different, services, 0);

        var first = Run(CheckId.Semantics.EmbeddingSimilarity, context);
        backend.Calls.Should().Be(2);
        var second = Run(CheckId.Semantics.EmbeddingSimilarity, context);
        backend.Calls.Should().Be(2);
        second.Should().Equal(first);

        Run(CheckId.Semantics.ReverseTranslation, context);
        reverse.Calls.Should().Be(1);
        var compared = Run(CheckId.Semantics.ReverseComparison, context);
        reverse.Calls.Should().Be(1);
        backend.Calls.Should().Be(4);
        Run(CheckId.Semantics.ReverseComparison, context).Should().Equal(compared);
        backend.Calls.Should().Be(4);
        Run(CheckId.Semantics.ReverseTranslation, context);
        reverse.Calls.Should().Be(1);

        var again = Admitted(Different, services, 0);
        Run(CheckId.Semantics.EmbeddingSimilarity, again).Should().Equal(first);
        Run(CheckId.Semantics.ReverseComparison, again).Should().Equal(compared);
        backend.Calls.Should().Be(4);
        reverse.Calls.Should().Be(1);
        services.Cache.Hits.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Unavailable_services_skip_with_a_reason_and_produce_nothing()
    {
        var context = SemanticFixtures.Context(Source, Different);
        EscalationGate.Admit(context, [SemanticFixtures.Flag(context, 0)], 24);

        var services = SemanticPorts.For(context);
        services.Embeddings.Available.Should().BeFalse();
        new EmbeddingSimilarityCheck().SkipReason(services).Should().Contain(SemanticPorts.NotAttachedReason);
        new ReverseComparisonCheck().SkipReason(services).Should().NotBeNull();

        foreach (var check in CheckRegistry.Default.ForCategory(CheckId.Semantics.Category))
        {
            check.Run(context).Should().BeEmpty();
        }

        EmbeddingModelStore.Find(null).Should().BeNull();
        EmbeddingModelStore.MissingReason(null).Should().Contain(EmbeddingModelDescription.MultilingualE5Small.FileName);
        EmbeddingModelDescription.MultilingualE5Small.License.Should().Be("MIT");
    }

    [Fact]
    public void An_exempt_span_is_never_admitted_and_findings_are_deterministic()
    {
        var (services, _, _) = SemanticFixtures.Services();
        var exempt = new ExemptSpan(new CheckRange("/0", 0, Different[0].Length), ExemptionReason.SettingsRule, Different[0]);
        var context = SemanticFixtures.Context(Source, Different, [exempt]);
        SemanticPorts.Attach(context, services);

        EscalationGate.Admit(context, [SemanticFixtures.Flag(context, 0)], 24).Admitted.Should().BeEmpty();

        var first = CheckRegistry.Default.ForCategory(CheckId.Semantics.Category).SelectMany(c => c.Run(Admitted(Different, services, 0, 1))).ToList();
        var second = CheckRegistry.Default.ForCategory(CheckId.Semantics.Category).SelectMany(c => c.Run(Admitted(Different, services, 0, 1))).ToList();

        first.Should().NotBeEmpty();
        second.Should().Equal(first);
        first.Should().OnlyContain(f => f.Severity == CheckSeverity.Score && f.Action == CheckAction.ScoreOnly);
    }
}
