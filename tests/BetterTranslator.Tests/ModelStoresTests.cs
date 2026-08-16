using System.IO;
using System.Linq;
using BetterTranslator.Engine.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Where models already are on this machine.
///
/// The point is not tidiness. A user who already has models in the store another
/// tool populated should not download them a second time, and on the machine
/// this was built against that was 33 GiB of 47.
/// </summary>
public sealed class ModelStoresTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-stores-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BETTERTRANSLATOR_MODEL_STORE", null);

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TheChosenFolderLeads()
    {
        // What the user chose here outranks what another tool decided elsewhere.
        var candidates = ModelStores.Candidates(_root);

        candidates.Should().NotBeEmpty();
        candidates[0].Path.Should().Be(Path.GetFullPath(_root));
        candidates[0].Rule.Should().Be("chosen");
    }

    [Fact]
    public void TheSharedDefaultIsAlwaysAmongTheCandidates()
    {
        ModelStores.Candidates(_root).Select(c => c.Path)
            .Should().Contain(Path.GetFullPath(ModelStores.SharedDefault()));
    }

    [Fact]
    public void AnEnvironmentOverrideIsHonoured()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Environment.SetEnvironmentVariable("BETTERTRANSLATOR_MODEL_STORE", elsewhere);

        var candidates = ModelStores.Candidates(_root);

        candidates.Select(c => c.Path).Should().Contain(Path.GetFullPath(elsewhere));
        candidates.Single(c => c.Path == Path.GetFullPath(elsewhere)).Rule.Should().Be("environment");
    }

    [Fact]
    public void TheSameFolderIsNeverListedTwice()
    {
        // Scanning one store twice would offer every model in it twice.
        Environment.SetEnvironmentVariable("BETTERTRANSLATOR_MODEL_STORE", _root);

        var candidates = ModelStores.Candidates(_root);

        candidates.Select(c => c.Path).Should().OnlyHaveUniqueItems();
        candidates[0].Rule.Should().Be("chosen", "the first rule to claim a path keeps it");
    }

    [Fact]
    public void AnEmptyOrUnusablePathIsNotACandidate()
    {
        Environment.SetEnvironmentVariable("BETTERTRANSLATOR_MODEL_STORE", "   ");

        ModelStores.Candidates(null).Should().NotContain(c => c.Rule == "environment");
    }

    [Fact]
    public void WritabilityIsProvedByWritingNotByInspectingPermissions()
    {
        // A folder can look permitted and still be refused by a redirected
        // profile, a full disk or a read-only mount -- and finding that out at
        // byte one of a fifteen-gigabyte download is far worse.
        var folder = Path.Combine(_root, "store");

        ModelStores.IsWritable(folder).Should().BeTrue();
        Directory.Exists(folder).Should().BeTrue("the writable check was allowed to create it");
    }

    [Fact]
    public void AReadOnlyCheckCreatesNothingButStillAnswers()
    {
        // Returning false for a not-yet-existing folder made the reference's own
        // check claim no store could be resolved at all.
        Directory.CreateDirectory(_root);
        var folder = Path.Combine(_root, "not", "yet", "there");

        ModelStores.IsWritable(folder, mayCreate: false).Should().BeTrue();
        Directory.Exists(folder).Should().BeFalse("a read-only check must create nothing");
    }

    [Fact]
    public void AProbeLeavesNothingBehind()
    {
        var folder = Path.Combine(_root, "clean");

        ModelStores.IsWritable(folder).Should().BeTrue();

        Directory.GetFiles(folder).Should().BeEmpty("the probe is removed, not left in the model store");
    }

    [Fact]
    public void AnUnreachablePathIsNotWritable()
    {
        ModelStores.IsWritable("").Should().BeFalse();
        ModelStores.IsWritable("   ").Should().BeFalse();

        // A folder whose parent is a file. Chosen over a made-up drive letter
        // because which letters exist is a property of the machine, not of this
        // code -- Z: is a real volume here.
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "x");

        ModelStores.IsWritable(Path.Combine(file, "models")).Should().BeFalse();
    }

    [Fact]
    public void TheProfileIsResolvedNotReadFromAnEnvironmentVariable()
    {
        // %USERPROFILE% alone is wrong or empty under a service account, a
        // scheduled task and some elevation paths -- exactly where a store lookup
        // silently resolves to nothing.
        var profile = ModelStores.UserProfile();

        profile.Should().NotBeEmpty();
        Directory.Exists(profile).Should().BeTrue();
    }
}
