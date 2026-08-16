using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Languages;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

[Collection(EngineConfigCollection.Name)]
public sealed class LanguageCatalogTests(ITestOutputHelper output) : IDisposable
{
    private const string FlagsDictionary =
        "pack://application:,,,/BetterTranslator;component/Themes/Flags.xaml";

    private static readonly LanguageCatalog Catalog = new();

    private static readonly string[] Models =
    [
        "translategemma-4b-it-Q4_K_M",
        "EuroLLM-9B-Instruct-Q4_K_M",
        "some-model-nobody-has-heard-of",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-languages", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EveryRegistryEntryResolvesToAFlagOrACodeBadge()
    {
        var listings = Catalog.For(null);

        listings.Should().NotBeEmpty();

        var empty = listings
            .Where(l => !(l.Asset.Kind == LanguageAssetKind.Flag && l.Asset.Key.Length > 0)
                     && l.Asset.Badge.Length == 0)
            .Select(l => l.Code)
            .ToList();

        output.WriteLine($"{listings.Count} entries, "
            + $"{listings.Count(l => l.Asset.IsFlag)} flags, "
            + $"{listings.Count(l => !l.Asset.IsFlag)} badges");

        empty.Should().BeEmpty("no picker row may render with nothing in its tile");
    }

    [Fact]
    public void EveryFlagKeyNamesArtworkThatIsActuallyShipped()
    {
        var keys = Catalog.For(null)
            .Where(l => l.Asset.IsFlag)
            .Select(l => l.Asset.Key)
            .Distinct()
            .ToArray();

        keys.Should().NotBeEmpty();

        var missing = StaRunner.Run(() =>
        {
            var flags = new ResourceDictionary { Source = new Uri(FlagsDictionary) };

            return keys.Where(k => flags[k] is not Brush).ToArray();
        });

        missing.Should().BeEmpty("a flag key with no DrawingBrush behind it paints nothing");
    }

    [Fact]
    public void NoTwoLanguagesShareTheSameFlagAndNoArtworkIsOrphaned()
    {
        var used = Catalog.For(null)
            .Where(l => l.Asset.IsFlag)
            .Select(l => l.Asset.Key)
            .ToList();

        used.Should().OnlyHaveUniqueItems("two languages one row apart cannot carry the same tile");

        var shipped = StaRunner.Run(() =>
        {
            var flags = new ResourceDictionary { Source = new Uri(FlagsDictionary) };

            return flags.Keys.OfType<string>().ToArray();
        });

        output.WriteLine($"{used.Count} flags in use, {shipped.Length} drawn");

        shipped.Should().BeEquivalentTo(used, "artwork nothing points at is artwork nobody checks");
    }

    [Fact]
    public void ACodeTheAssetTableDoesNotCarryTakesABadgeRatherThanAGuessedFlag()
    {
        var invented = new LanguageEntry
        {
            Code = "pt-BR",
            Name = "Portuguese (Brazil)",
            Native = "Português do Brasil",
            Script = "Latn",
        };

        var asset = new LanguageAssets().For(invented);

        asset.Kind.Should().Be(LanguageAssetKind.Badge);
        asset.Key.Should().BeEmpty();
        asset.Badge.Should().Be("PT-BR");
    }

    [Fact]
    public void EveryEntryInEveryModelCatalogExistsInTheRegistry()
    {
        var registry = Catalog.Registry.All.Select(l => l.Code).ToList();
        var families = Catalog.Models.All;

        families.Should().NotBeEmpty();

        foreach (var family in families)
        {
            output.WriteLine($"{family.Family} {family.Version}: {family.Codes.Count} codes, {family.Provenance}");

            family.Provenance.Should().NotBeEmpty($"{family.Family} must say where its language set came from");
            family.Codes.Should().BeSubsetOf(
                registry,
                $"{family.Family} may only reference registry codes");
        }

        Catalog.Models.For("EuroLLM-9B-Instruct-Q4_K_M")!.Codes.Should().NotBeEmpty();
    }

    [Fact]
    public void TheRegistryCarriesNoDuplicateCanonicalCode()
    {
        var codes = Catalog.Registry.All.Select(l => l.Code).ToList();

        codes.Should().OnlyHaveUniqueItems();
        codes.Select(c => c.ToUpperInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryEntryCarriesACodeNameEndonymScriptAndDirection()
    {
        var listings = Catalog.For(null);

        listings.Should().OnlyContain(l =>
            l.Code.Length > 0
            && l.Name.Length > 0
            && l.Endonym.Length > 0
            && l.Script.Length > 0
            && (l.Direction == "ltr" || l.Direction == "rtl"));
    }

    [Fact]
    public void TheAssetTableCoversTheRegistryAndNothingElse()
    {
        var assets = new LanguageAssets();
        var codes = Catalog.Registry.All.Select(l => l.Code).ToList();

        codes.Should().OnlyContain(c => assets.Declares(c), "every language must have a decided asset");
        assets.MappedCodes.Should().BeSubsetOf(codes, "the table may not carry a language the registry does not");
    }

    [Fact]
    public void ListLanguagesAndThePickerReturnTheSameSetForTheSameModel()
    {
        foreach (var model in Models)
        {
            var tool = LanguageTools.List(Catalog, model);
            var picker = TargetLanguage.ForCatalog(model);

            tool.Select(r => r.Code).Should().Equal(
                picker.Select(l => l.Code),
                $"list_languages and the picker read one registry for '{model}'");

            var available = tool.Where(r => r.Availability != "unsupported").Select(r => r.Code);

            available.Should().Equal(
                picker.Where(l => l.IsAvailable).Select(l => l.Code),
                $"both surfaces must agree on what '{model}' can translate into");

            tool.Should().OnlyContain(r => r.Reason.Length > 0);
            tool.Should().OnlyContain(r => r.Asset == "flag" || r.Asset == "badge");
        }
    }

    [Fact]
    public void SwitchingModelPartitionsTheListAndKeepsAStillSupportedSelection()
    {
        var workspace = Workspace();

        workspace.SetModelLanguages(TargetLanguage.ForCatalog("translategemma-4b-it-Q4_K_M"), "TranslateGemma");
        workspace.ChooseLanguageCommand.Execute(workspace.Languages.Single(l => l.Code == "cs"));
        workspace.Language.Code.Should().Be("cs");

        workspace.SetModelLanguages(TargetLanguage.ForCatalog("EuroLLM-9B-Instruct-Q4_K_M"), "EuroLLM");

        var unavailable = workspace.Languages.Where(l => l.IsUnavailable).ToList();

        output.WriteLine($"EuroLLM: {workspace.AvailableLanguageCount} available, {unavailable.Count} unavailable");
        output.WriteLine("unavailable: " + string.Join(", ", unavailable.Select(l => l.Code)));

        workspace.Languages.Should().HaveCount(TargetLanguage.All.Count, "nothing is hidden, only marked");
        unavailable.Should().NotBeEmpty("EuroLLM publishes a narrower set than the registry carries");
        unavailable.Should().OnlyContain(l => l.Reason.Length > 0, "an unavailable row must say why");
        workspace.AvailableLanguageCount.Should().Be(workspace.Languages.Count - unavailable.Count);

        workspace.Language.Code.Should().Be("cs", "Czech is in EuroLLM's set, so the selection survives");

        var stranded = TargetLanguage.ForCatalog("EuroLLM-9B-Instruct-Q4_K_M").First(l => l.IsUnavailable);

        workspace.SetModelLanguages(TargetLanguage.ForCatalog("translategemma-4b-it-Q4_K_M"), "TranslateGemma");
        workspace.ChooseLanguageCommand.Execute(workspace.Languages.Single(l => l.Code == stranded.Code));
        workspace.Language.Code.Should().Be(stranded.Code);

        workspace.ModelId = "EuroLLM-9B-Instruct-Q4_K_M";
        workspace.SetModelLanguages(TargetLanguage.ForCatalog("EuroLLM-9B-Instruct-Q4_K_M"), "EuroLLM");

        workspace.Language.Code.Should().Be(stranded.Code, "a model switch is not a choice about the pair");
        workspace.CanSend.Should().BeFalse("the model cannot do that target, so the send is refused instead");
        workspace.DirectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AnUnavailableRowCannotBeChosen()
    {
        var workspace = Workspace();

        workspace.SetModelLanguages(TargetLanguage.ForCatalog("EuroLLM-9B-Instruct-Q4_K_M"), "EuroLLM");

        var before = workspace.Language.Code;
        var unavailable = workspace.Languages.First(l => l.IsUnavailable);

        workspace.ChooseLanguageCommand.Execute(unavailable);

        workspace.Language.Code.Should().Be(before);
    }

    [Fact]
    public void AModelWithNoPublishedSetLeavesEveryLanguageAvailableAndSaysSo()
    {
        var listings = Catalog.For("translategemma-4b-it-Q4_K_M");

        listings.Should().OnlyContain(l => l.Availability == LanguageAvailability.Unverified);
        listings.Should().OnlyContain(l => l.Reason.Contains("unverified", StringComparison.OrdinalIgnoreCase));
        listings.Should().OnlyContain(l => l.IsAvailable);
    }

    private ChatWorkspaceViewModel Workspace()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null);
    }
}
