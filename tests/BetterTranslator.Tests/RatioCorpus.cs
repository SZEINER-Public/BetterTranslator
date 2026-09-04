using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BetterTranslator.Core.Verification.Checks.Ratio;

namespace BetterTranslator.Tests;

public static class RatioCorpus
{
    public const string FixtureRelativePath = "tests/BetterTranslator.Tests/Fixtures/Parallel/en-cs.json";

    public const string ProfileRelativePath = "src/BetterTranslator.Core/Verification/Checks/Ratio/ratio-profile.json";

    public const string WriteProfileVariable = "BT_WRITE_RATIO_PROFILE";

    private static readonly (string Resource, string Path)[] TermTables =
    [
        ("BetterTranslator.Engine.Data.domain-terms-cs.md", "src/BetterTranslator.Engine/Data/domain-terms-cs.md"),
        ("BetterTranslator.Engine.Data.glossary-cs.md", "src/BetterTranslator.Engine/Data/glossary-cs.md"),
    ];

    public static CalibrationSettings Settings { get; } = new(PrecisionFloor: 0.7, BandQuantile: 0, HeldOutEvery: 4, ScoreWeight: 0.5);

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BetterTranslator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found above the test output folder");
    }

    public static (IReadOnlyList<ParallelSample> Samples, IReadOnlyList<RatioSource> Sources) Load()
    {
        var samples = new List<ParallelSample>();
        var sources = new List<RatioSource>();

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parallel", "en-cs.json");
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var pair = document.RootElement.GetProperty("pair").GetString()!;
        var fromFixture = 0;

        foreach (var element in document.RootElement.GetProperty("pairs").EnumerateArray())
        {
            samples.Add(new ParallelSample(
                pair,
                element.GetProperty("unit").GetString()!,
                element.GetProperty("source").GetString()!,
                element.GetProperty("target").GetString()!,
                element.GetProperty("origin").GetString()!));
            fromFixture++;
        }

        sources.Add(new RatioSource(FixtureRelativePath, fromFixture));

        var engine = typeof(BetterTranslator.Engine.Documents.ContentTranslation).Assembly;

        foreach (var (resource, path) in TermTables)
        {
            using var stream = engine.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource + " is not embedded");
            using var reader = new StreamReader(stream);
            var terms = RatioCalibration.TermPairsFromTable(reader.ReadToEnd(), pair, path);
            samples.AddRange(terms);
            sources.Add(new RatioSource(path, terms.Count));
        }

        return (samples, sources);
    }

    public static RatioProfile Calibrate()
    {
        var (samples, sources) = Load();
        return RatioCalibration.Calibrate(samples, Settings, sources);
    }

    public static bool WriteRequested() =>
        string.Equals(Environment.GetEnvironmentVariable(WriteProfileVariable), "1", StringComparison.Ordinal);

    public static string WriteProfile(RatioProfile profile)
    {
        var path = Path.Combine(RepositoryRoot(), ProfileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(path, profile.Serialize());
        return path;
    }
}
