using System.Linq;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Ratio;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RatioCalibrationTests
{
    [Fact]
    public void The_committed_profile_is_what_the_repository_data_produces()
    {
        var computed = RatioCorpus.Calibrate();

        if (RatioCorpus.WriteRequested())
        {
            RatioCorpus.WriteProfile(computed);
            return;
        }

        RatioProfile.Default.Serialize().Should().Be(computed.Serialize());
    }

    [Fact]
    public void Every_band_carries_its_sample_count_and_every_check_its_held_out_precision_and_recall()
    {
        var profile = RatioProfile.Default;

        profile.Bands.Should().NotBeEmpty();
        profile.Bands.Should().OnlyContain(b => b.Samples > 0 && b.LengthRatio.Samples > 0 && b.LengthRatio.Low <= b.LengthRatio.High);
        profile.Bands.Select(b => (b.Pair, b.Unit)).Should().OnlyHaveUniqueItems();
        profile.Bands.Should().Contain(b => b.Pair == "en-cs" && b.Unit == RatioUnitType.Sentence);
        profile.Bands.Should().Contain(b => b.Pair == "en-cs" && b.Unit == RatioUnitType.Word);
        profile.Sources.Should().NotBeEmpty();
        profile.Sources.Sum(s => s.Pairs).Should().Be(RatioCorpus.Load().Samples.Count);

        profile.Checks.Select(c => c.Id).Should().Equal(
            CheckId.Ratio.LengthRatio,
            CheckId.Ratio.Truncation,
            CheckId.Ratio.Repetition,
            CheckId.Ratio.Compression,
            CheckId.Ratio.Insertion);

        profile.Checks.Should().OnlyContain(c =>
            c.Precision >= 0 && c.Precision <= 1
            && c.Recall >= 0 && c.Recall <= 1
            && c.HeldOutPositives > 0
            && c.HeldOutNegatives > 0
            && c.Enabled == (c.Precision >= profile.PrecisionFloor));
    }

    [Fact]
    public void The_held_out_split_is_deterministic_and_disjoint()
    {
        var (samples, _) = RatioCorpus.Load();
        var heldOut = samples.Where(s => RatioCalibration.IsHeldOut(s, RatioCorpus.Settings.HeldOutEvery)).ToList();

        heldOut.Should().NotBeEmpty();
        heldOut.Count.Should().BeLessThan(samples.Count);
        heldOut.Should().Equal(samples.Where(s => RatioCalibration.IsHeldOut(s, RatioCorpus.Settings.HeldOutEvery)));
    }

    [Fact]
    public void Term_tables_yield_word_pairs_and_skip_do_not_translate_rows()
    {
        const string Table =
            """
            | English | Czech | Wrong renderings | Why |
            |---|---|---|---|
            | engine | engine | motor; stroj | keeps engine |
            | build | sestavení | stavba | the compiled output |
            | runtime | - | | do not translate |
            """;

        var pairs = RatioCalibration.TermPairsFromTable(Table, "en-cs", "table");

        pairs.Select(p => (p.Source, p.Target)).Should().Equal(("engine", "engine"), ("build", "sestavení"));
        pairs.Should().OnlyContain(p => p.Unit == RatioUnitType.Word && p.Pair == "en-cs");
    }

    [Fact]
    public void A_check_below_the_precision_floor_ships_disabled_and_emits_nothing()
    {
        var profile = RatioProfile.Default;
        var disabled = profile with
        {
            Checks = [.. profile.Checks.Select(c => c with { Enabled = false })],
        };

        var check = new LengthRatioCheck(disabled);
        var context = RatioFixtures.Context(["Open the settings window and save the changes."], ["Otevřete okno nastavení a uložte změny. Otevřete okno nastavení a uložte změny. Otevřete okno nastavení a uložte změny."]);

        check.Run(context).Should().BeEmpty();
        new LengthRatioCheck(RatioFixtures.AllEnabled).Run(context).Should().NotBeEmpty();
    }
}
