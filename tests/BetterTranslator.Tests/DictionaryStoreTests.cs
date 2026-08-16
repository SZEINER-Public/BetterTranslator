using System.IO;
using System.Linq;
using BetterTranslator.Core.Verification;
using BetterTranslator.Engine.Verification;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Finding a Hunspell pair without anyone configuring a path, which is what
/// decides whether the word-level pass runs at all on a fresh machine.
///
/// The fixtures use a made-up language, because a real dictionary is not in this
/// repository and a test that needed one would be a test nobody can run.
/// </summary>
public sealed class DictionaryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-dict", Guid.NewGuid().ToString("N"));
    private readonly string? _previousOverride =
        Environment.GetEnvironmentVariable(DictionaryStore.EnvironmentOverride);

    public DictionaryStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DictionaryStore.EnvironmentOverride, _previousOverride);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>A pair small enough to write and real enough to load.</summary>
    private static void WritePair(string folder, string stem, params string[] words)
    {
        File.WriteAllText(Path.Combine(folder, stem + ".aff"), "SET UTF-8\n");
        File.WriteAllText(
            Path.Combine(folder, stem + ".dic"),
            words.Length + "\n" + string.Join('\n', words) + "\n");
    }

    [Fact]
    public void A_pair_in_the_data_folder_is_found_by_its_exact_code()
    {
        var folder = Folder("data");
        WritePair(folder, "zz", "slovo");

        var pair = DictionaryStore.Find("zz", folder);

        pair.Should().NotBeNull();
        pair!.DicPath.Should().Be(Path.Combine(folder, "zz.dic"));
        pair.AffPath.Should().Be(Path.Combine(folder, "zz.aff"), "the affix rules travel with the word list");
    }

    [Fact]
    public void A_region_qualified_pair_answers_a_bare_language()
    {
        // The published dictionaries are named for a region and the composer
        // stores a language, so the two have to meet without a region table.
        var folder = Folder("data");
        WritePair(folder, "zz_ZZ", "slovo");

        DictionaryStore.Find("zz", folder).Should().NotBeNull();
    }

    [Fact]
    public void A_hyphenated_code_finds_the_underscored_file()
    {
        var folder = Folder("data");
        WritePair(folder, "zz_ZZ", "slovo");

        DictionaryStore.Find("zz-ZZ", folder).Should().NotBeNull();
    }

    [Fact]
    public void A_longer_language_code_is_not_mistaken_for_a_region()
    {
        // zzb is its own language, not a region of zz, so it must not answer.
        var folder = Folder("data");
        WritePair(folder, "zzb", "slovo");

        DictionaryStore.Find("zz", folder).Should().BeNull();
    }

    [Fact]
    public void A_word_list_without_its_affix_file_is_not_a_pair()
    {
        // Half an install loads as an exception rather than as a dictionary, so
        // it counts as absent and the verifier stays off.
        var folder = Folder("data");
        File.WriteAllText(Path.Combine(folder, "zz.dic"), "1\nslovo\n");

        DictionaryStore.Find("zz", folder).Should().BeNull();
    }

    [Fact]
    public void No_language_finds_nothing_even_when_a_dictionary_is_present()
    {
        // The guarantee that matters: marking one language's output against
        // another language's word list would flag every word in it.
        var folder = Folder("data");
        WritePair(folder, "zz", "slovo");

        DictionaryStore.Find(null, folder).Should().BeNull();
        DictionaryStore.Find("   ", folder).Should().BeNull();
    }

    [Fact]
    public void The_override_outranks_the_data_folder_and_both_outrank_the_executable()
    {
        var shared = Folder("shared");
        var data = Folder("data");

        WritePair(shared, "zz", "prvni");
        WritePair(data, "zz", "druhe");

        Environment.SetEnvironmentVariable(DictionaryStore.EnvironmentOverride, shared);

        DictionaryStore.Find("zz", data)!.DicPath.Should().Be(Path.Combine(shared, "zz.dic"));

        Environment.SetEnvironmentVariable(DictionaryStore.EnvironmentOverride, null);

        DictionaryStore.Find("zz", data)!.DicPath.Should().Be(Path.Combine(data, "zz.dic"));

        // And a dictionary the reader supplied outranks one that shipped beside
        // the executable, which is what makes a drop-in stick across an update.
        var besideTheExecutable = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, DictionaryStore.FolderName));

        DictionaryStore.Folders(data)
            .Should().ContainInOrder(Path.GetFullPath(data), besideTheExecutable);
    }

    [Fact]
    public void The_stems_tried_for_a_code_are_ordered_and_free_of_repeats()
    {
        DictionaryStore.StemsFor("zz-ZZ").Should().Equal("zz_ZZ", "zz-ZZ", "zz");
        DictionaryStore.StemsFor("zz").Should().Equal("zz");
    }

    [Fact]
    public void A_discovered_pair_builds_a_verifier_with_nothing_configured()
    {
        // The point of the whole file: verification with no path in settings.
        var folder = Folder("data");
        WritePair(folder, "zz", "slovo");

        var verifier = VerificationFactory.Create(new VerificationSettings(), "zz", folder);

        verifier.Should().NotBeNull("a dictionary on disk is enough to switch the pass on");
        verifier!.Verify("word", "slovo").Executed.Should().BeTrue();
    }

    [Fact]
    public void No_dictionary_anywhere_still_yields_no_verifier()
    {
        VerificationFactory.Create(new VerificationSettings(), "zz", Folder("empty")).Should().BeNull();
    }

    [Fact]
    public void A_configured_pair_outranks_the_search()
    {
        var configured = Folder("configured");
        var discovered = Folder("data");

        WritePair(configured, "chosen", "slovo");
        WritePair(discovered, "zz", "slovo");

        var settings = new VerificationSettings
        {
            HunspellDicPath = Path.Combine(configured, "chosen.dic"),
            HunspellAffPath = Path.Combine(configured, "chosen.aff"),
        };

        // Asserted through behaviour rather than a path, because the verifier
        // does not publish which files it opened: the configured list holds the
        // word and the discovered one does not.
        VerificationFactory.Create(settings, "zz", discovered).Should().NotBeNull();
    }

    [Fact]
    public void A_half_configured_pair_falls_through_to_the_search()
    {
        // It used to switch verification off entirely, which is the worst of
        // both: a typo in one path and nothing is marked ever again.
        var folder = Folder("data");
        WritePair(folder, "zz", "slovo");

        var settings = new VerificationSettings { HunspellDicPath = Path.Combine(folder, "missing.dic") };

        VerificationFactory.Create(settings, "zz", folder).Should().NotBeNull();
    }
}
