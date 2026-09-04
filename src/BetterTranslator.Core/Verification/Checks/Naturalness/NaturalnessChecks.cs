using BetterTranslator.Core.Verification.Checks.Coverage;

namespace BetterTranslator.Core.Verification.Checks.Naturalness;

public sealed class AlignmentCrossingCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.AlignmentCrossing;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;

        foreach (var pair in sentences.Select(s => s.Unit).Distinct())
        {
            var profile = ProfileFor(context, services, pair);

            if (profile is null || !profile.Crossing.Calibrated || pair.Confidence < CoverageAlignment.SentenceCeilingFloor)
            {
                continue;
            }

            var measure = AlignmentCrossing.Compute(pair);

            if (measure.SourceWords < profile.Crossing.MinimumTokens || measure.Matched < 3)
            {
                continue;
            }

            if (measure.Normalized < profile.Crossing.Limit)
            {
                yield return new NaturalnessHit(
                    pair.TargetRange!,
                    pair.SourceRange,
                    Fill(rule, ("crossings", measure.Crossings), ("tokens", measure.Matched), ("limit", profile.Crossing.Limit)));
            }
        }
    }
}

public sealed class TagSequenceDivergenceCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.TagSequenceDivergence;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;

        foreach (var sentence in sentences)
        {
            var profile = ProfileFor(context, services, sentence.Unit);

            if (profile is null || !profile.TagDivergence.Calibrated || sentence.Words.Count < profile.TagDivergence.MinimumTokens)
            {
                continue;
            }

            var tags = TagSequences.TagsOf(services.Tagger, sentence.Words);

            if (tags.Count(t => t != "unknown") < profile.TagDivergence.MinimumTokens)
            {
                continue;
            }

            var divergence = TagSequences.Divergence(TagSequences.Bigrams(tags), profile.TagReference);

            if (divergence > profile.TagDivergence.Limit)
            {
                yield return new NaturalnessHit(sentence.Range, sentence.Unit.SourceRange, Fill(rule, ("divergence", divergence), ("limit", profile.TagDivergence.Limit)));
            }
        }
    }
}

public sealed class CliticPlacementCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.CliticPlacement;

    protected override CheckGranularity Granularity => CheckGranularity.Word;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;
        var inventory = new HashSet<string>(pack.AllClitics, StringComparer.OrdinalIgnoreCase);
        var homographs = new HashSet<string>(pack.CliticHomographs, StringComparer.OrdinalIgnoreCase);
        var pronoun = pack.Tag("pronoun");
        var tagger = services.Tagger;
        bool IsClitic(CoverageToken token) =>
            inventory.Contains(token.Text)
            && (!homographs.Contains(token.Text) || (tagger.Available && pronoun.Length > 0 && tagger.Tags(token.Text).Any(t => t.StartsWith(pronoun, StringComparison.Ordinal))));
        var clitics = new Func<CoverageToken, bool>(IsClitic);

        foreach (var sentence in sentences)
        {
            foreach (var clause in Clauses(sentence, pack, context.Target.Text))
            {
                if (clause.Count < 2)
                {
                    continue;
                }

                if (clitics(clause[0]))
                {
                    yield return new NaturalnessHit(clause[0].Range, sentence.Unit.SourceRange, Fill(rule, ("form", clause[0].Text), ("position", "in first position of the clause")));
                }

                if (clitics(clause[^1]) && !clitics(clause[0]))
                {
                    yield return new NaturalnessHit(clause[^1].Range, sentence.Unit.SourceRange, Fill(rule, ("form", clause[^1].Text), ("position", "in final position of the clause")));
                }

                var previousGroup = -1;
                CoverageToken? previous = null;

                foreach (var token in clause)
                {
                    var group = clitics(token) ? pack.CliticGroupIndex(token.Text) : -1;

                    if (group < 0)
                    {
                        previousGroup = -1;
                        previous = null;
                        continue;
                    }

                    if (previous is not null && group < previousGroup)
                    {
                        yield return new NaturalnessHit(token.Range, sentence.Unit.SourceRange, Fill(rule, ("form", token.Text), ("position", "out of cluster order after '" + previous.Text + "'")));
                    }

                    previousGroup = group;
                    previous = token;
                }
            }

            if (pack.TwoWordConditional is { } conditional)
            {
                foreach (var word in sentence.Words.Where(w => conditional.ContractedForms.Contains(w.Text, StringComparer.OrdinalIgnoreCase)))
                {
                    yield return new NaturalnessHit(word.Range, sentence.Unit.SourceRange, Fill(rule, ("form", word.Text), ("position", "contracted where the conditional is two words: " + conditional.Particle + " " + string.Join("/", conditional.Auxiliaries))));
                }
            }
        }
    }
}

public sealed class PronounExplicitnessCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.PronounExplicitness;

    protected override CheckGranularity Granularity => CheckGranularity.Word;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;

        if (!pack.ProDrop)
        {
            yield break;
        }

        var pronouns = new HashSet<string>(pack.SubjectPronouns, StringComparer.OrdinalIgnoreCase);
        var verb = pack.Tag("verb");
        var person = pack.Tag("person");

        foreach (var sentence in sentences)
        {
            foreach (var clause in Clauses(sentence, pack, context.Target.Text))
            {
                if (clause.Count < 2 || !pronouns.Contains(clause[0].Text))
                {
                    continue;
                }

                var next = services.Tagger.Tags(clause[1].Text);

                if (next.Any(t => t.StartsWith(verb, StringComparison.Ordinal) && t.Contains(person, StringComparison.Ordinal)))
                {
                    yield return new NaturalnessHit(clause[0].Range, sentence.Unit.SourceRange, Fill(rule, ("form", clause[0].Text)));
                }
            }
        }
    }
}

public sealed class NominalStyleCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.NominalStyle;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;
        var stems = pack.LightVerbs.Select(v => v.TrimEnd('t', 'ť').ToLowerInvariant()).Where(s => s.Length >= 4).ToList();

        foreach (var sentence in sentences)
        {
            for (var i = 0; i + 1 < sentence.Words.Count; i++)
            {
                var word = sentence.Words[i].Key;

                if (!stems.Any(s => word.StartsWith(s, StringComparison.Ordinal)))
                {
                    continue;
                }

                var noun = sentence.Words.Skip(i + 1).Take(3).FirstOrDefault(w => pack.DeverbalSuffixes.Any(s => w.Key.EndsWith(s, StringComparison.Ordinal)));

                if (noun is not null)
                {
                    yield return new NaturalnessHit(sentence.Range, sentence.Unit.SourceRange, Fill(rule, ("form", sentence.Words[i].Text), ("noun", noun.Text)));
                }
            }
        }
    }
}

public sealed class PassiveCalqueCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.PassiveCalque;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;
        var auxiliaries = new HashSet<string>(pack.PassiveAuxiliaries, StringComparer.OrdinalIgnoreCase);
        var participle = pack.Tag("passiveParticiple");

        foreach (var sentence in sentences)
        {
            for (var i = 0; i < sentence.Words.Count; i++)
            {
                if (!auxiliaries.Contains(sentence.Words[i].Text))
                {
                    continue;
                }

                var candidate = sentence.Words.Skip(i + 1).Take(2).FirstOrDefault(w => participle.Length > 0 && services.Tagger.Tags(w.Text).Any(t => t.Contains(participle, StringComparison.Ordinal)));

                if (candidate is not null)
                {
                    yield return new NaturalnessHit(sentence.Range, sentence.Unit.SourceRange, Fill(rule, ("form", sentence.Words[i].Text), ("participle", candidate.Text)));
                }
            }
        }
    }
}

public sealed class RegisterConsistencyCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.RegisterConsistency;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;

        if (!services.Tagger.Available)
        {
            yield break;
        }

        var document = RegisterProfiles.Document(context, pack, services.Tagger);

        foreach (var sentence in sentences)
        {
            var segment = RegisterProfiles.Of(TagSequences.TagsOf(services.Tagger, sentence.Words), pack);
            var deviations = segment.Deviations(document);

            if (deviations.Count > 0)
            {
                yield return new NaturalnessHit(sentence.Range, sentence.Unit.SourceRange, Fill(rule, ("segment", string.Join(", ", deviations)), ("document", Describe(document))));
            }
        }
    }

    private static string Describe(RegisterProfile profile) =>
        string.Join(", ", new[] { profile.Formality, profile.Person, profile.Instruction, profile.Tense }.Where(v => v != RegisterProfile.Unknown));
}

public sealed class SentenceBoundaryFitCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.SentenceBoundaryFit;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;
        var store = NaturalnessEvidenceStore.For(context);

        foreach (var pair in sentences.Select(s => s.Unit).Distinct())
        {
            var profile = ProfileFor(context, services, pair);

            if (profile is null || !profile.SentenceExcess.Calibrated)
            {
                continue;
            }

            var fit = SentenceBoundaryFit.Compute(pair);

            if (store.ExpectedSentences.TryGetValue(pair.Identity, out var expected) && expected == fit.Target)
            {
                continue;
            }

            if (Math.Abs(fit.Excess) > profile.SentenceExcess.Limit)
            {
                yield return new NaturalnessHit(pair.TargetRange!, pair.SourceRange, Fill(rule, ("source", fit.Source), ("target", fit.Target), ("limit", profile.SentenceExcess.Limit)));
            }
        }
    }
}

public sealed class TitleCaseAndQuotationCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.TitleCaseAndQuotation;

    protected override CheckGranularity Granularity => CheckGranularity.Word;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;

        foreach (var heading in context.Target.OfKind(DocumentNodeKind.Heading))
        {
            var words = CoverageTokenizer.Tokenize(context.Target.Text, heading.Range).Where(t => t.Kind == CoverageTokenKind.Word).ToList();
            var content = words.Skip(1).Where(w => w.Text.Length > 3).ToList();

            if (words.Count >= 3 && content.Count >= 2 && content.All(w => char.IsUpper(w.Text[0])))
            {
                yield return new NaturalnessHit(heading.Range, null, Fill(rule, ("kind", "title case heading"), ("form", context.Target.Text.Substring(heading.Range.Offset, heading.Range.Length).Trim())));
            }
        }

        foreach (var sentence in sentences)
        {
            var text = context.Target.Text.Substring(sentence.Range.Offset, sentence.Range.Length);

            foreach (var foreign in pack.ForeignQuotationPairs.Where(p => p.Count == 2))
            {
                if (string.Equals(foreign[0], pack.QuotationOpen, StringComparison.Ordinal) && string.Equals(foreign[1], pack.QuotationClose, StringComparison.Ordinal))
                {
                    continue;
                }

                var open = text.IndexOf(foreign[0], StringComparison.Ordinal);
                var close = open < 0 ? -1 : text.IndexOf(foreign[1], open + foreign[0].Length, StringComparison.Ordinal);

                if (open >= 0 && close > open)
                {
                    var range = new CheckRange(sentence.Range.UnitPath, sentence.Range.Offset + open, close - open + foreign[1].Length);

                    if (!sentence.Unit.TargetHidden.Any(h => h.Overlaps(range)))
                    {
                        yield return new NaturalnessHit(range, sentence.Unit.SourceRange, Fill(rule, ("kind", "quotation pair " + foreign[0] + foreign[1] + " where the language uses " + pack.QuotationOpen + pack.QuotationClose), ("form", text.Substring(open, close - open + foreign[1].Length))));
                    }
                }
            }
        }
    }
}

public sealed class PluralCategoryCoverageCheck : NaturalnessCheck
{
    public override string CheckId => Checks.CheckId.Naturalness.PluralCategoryCoverage;

    protected override IEnumerable<NaturalnessHit> Examine(CheckContext context, NaturalnessServices services, NaturalnessPack pack, IReadOnlyList<NaturalnessSentence> sentences)
    {
        var rule = pack.Rule(CheckId)!;

        if (!string.Equals(context.Adapter.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var pair in sentences.Select(s => s.Unit).Distinct())
        {
            var marker = pack.PluralMarkers.FirstOrDefault(m => pair.SourceRaw.Contains(m, StringComparison.Ordinal));

            if (marker is not null)
            {
                yield return new NaturalnessHit(pair.TargetRange!, pair.SourceRange, Fill(rule, ("form", marker)), 60);
            }
        }
    }
}
