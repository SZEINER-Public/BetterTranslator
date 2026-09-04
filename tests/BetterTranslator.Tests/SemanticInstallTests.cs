using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Core.Services;
using BetterTranslator.Core.Verification;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Semantics;
using BetterTranslator.Core.Verification.Gate;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Engine.Verification.Structure;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using BetterTranslator.Runtime.Verification;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class SemanticInstallTests : IDisposable
{
    private static readonly string[] Source = ["The file is on disk.", "Save changes."];

    private static readonly string[] Different = ["Pilník je na disku.", "Uložit změny."];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-semantic-install", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private InstallPaths Paths(string leaf)
    {
        var paths = new InstallPaths(new AppPaths(Path.Combine(_root, leaf)));
        paths.EnsureCreated();
        return paths;
    }

    private static byte[] Bytes(int length, byte seed)
    {
        var body = new byte[length];

        for (var i = 0; i < length; i++)
        {
            body[i] = (byte)((i * 7 + seed) % 251);
        }

        return body;
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ModelComponent FixtureComponent(Uri baseAddress, byte[] model, byte[] tokenizer)
    {
        var declared = SemanticRuntime.EmbeddingComponent();

        return declared with
        {
            SizeBytes = model.Length,
            Sha256 = Sha256Of(model),
            DownloadUrl = new Uri(baseAddress, "/model.onnx"),
            Companions =
            [
                declared.Companions.Single() with
                {
                    SizeBytes = tokenizer.Length,
                    Sha256 = Sha256Of(tokenizer),
                    DownloadUrl = new Uri(baseAddress, "/tokenizer.json"),
                },
            ],
        };
    }

    [Fact]
    public void The_catalogue_offers_the_embedding_model_as_optional_and_fetchable_with_its_tokenizer()
    {
        var component = ComponentCatalog.Optional.Should().ContainSingle().Subject;

        component.Id.Should().Be("embedding-multilingual-e5-small");
        component.Kind.Should().Be(ComponentKind.Verification);
        component.IsRequired.Should().BeFalse();
        component.PreSelected.Should().BeFalse();
        component.IsFetchable.Should().BeTrue();
        component.FileName.Should().Be(EmbeddingModelDescription.MultilingualE5Small.FileName);
        component.SizeBytes.Should().Be(SemanticRuntime.ModelBytes);
        component.Sha256.Should().Be(SemanticRuntime.ModelSha256);
        component.DeclaredIntegrity.Should().NotBeNull();
        component.Summary.Should().Contain(EmbeddingModelDescription.MultilingualE5Small.License);

        var tokenizer = component.Companions.Should().ContainSingle().Subject;
        tokenizer.FileName.Should().Be(EmbeddingModelStore.TokenizerFileName);
        tokenizer.SizeBytes.Should().Be(SemanticRuntime.TokenizerBytes);
        tokenizer.Sha256.Should().Be(SemanticRuntime.TokenizerSha256);
        tokenizer.DownloadUrl.Should().NotBeNull();

        new ComponentCatalog().All.Should().Contain(c => c.Id == component.Id);
        ComponentCatalog.BuiltIn.Should().NotContain(c => c.Kind == ComponentKind.Verification);
    }

    [Fact]
    public async Task Installing_through_the_queue_lands_both_files_where_the_store_looks()
    {
        var model = Bytes(8192, 3);
        var tokenizer = Bytes(2048, 11);

        using var server = new StubHttpServer(request => new StubResponse
        {
            Body = request.Path.EndsWith("tokenizer.json", StringComparison.Ordinal) ? tokenizer : model,
        });

        var paths = Paths("install");
        var resolver = new ModelResolver(paths, new ModelLibrary());
        using var client = server.CreateClient();
        var queue = new ComponentInstallQueue(new DownloadManager(client, paths, resolver), resolver, paths);
        var component = FixtureComponent(server.BaseAddress, model, tokenizer);

        EmbeddingModelStore.Find(paths.ModelsFolder).Should().BeNull();

        var result = await queue.EnqueueAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed, result.Failure);
        paths.IsInstalled(component).Should().BeTrue();

        var files = EmbeddingModelStore.Find(paths.ModelsFolder);
        files.Should().NotBeNull();
        files!.ModelPath.Should().Be(paths.PathFor(component));
        files.TokenizerPath.Should().Be(paths.PathFor(component.Companions.Single()));
        (await File.ReadAllBytesAsync(files.ModelPath)).Should().Equal(model);
        (await File.ReadAllBytesAsync(files.TokenizerPath)).Should().Equal(tokenizer);
    }

    [Fact]
    public async Task A_download_that_fails_its_checksum_leaves_nothing_where_the_store_looks()
    {
        var model = Bytes(8192, 3);
        var tokenizer = Bytes(2048, 11);
        var corrupted = Bytes(8192, 4);

        using var server = new StubHttpServer(request => new StubResponse
        {
            Body = request.Path.EndsWith("tokenizer.json", StringComparison.Ordinal) ? tokenizer : corrupted,
        });

        var paths = Paths("corrupt");
        var resolver = new ModelResolver(paths, new ModelLibrary());
        using var client = server.CreateClient();
        var queue = new ComponentInstallQueue(new DownloadManager(client, paths, resolver), resolver, paths);
        var component = FixtureComponent(server.BaseAddress, model, tokenizer);

        var result = await queue.EnqueueAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        result.Failure.Should().Contain("checksum");
        File.Exists(paths.PathFor(component)).Should().BeFalse();
        File.Exists(paths.PathFor(component) + ".part").Should().BeFalse();
        EmbeddingModelStore.Find(paths.ModelsFolder).Should().BeNull();
        EmbeddingModelStore.Host(paths.ModelsFolder).Available.Should().BeFalse();
    }

    [Fact]
    public async Task The_span_cap_round_trips_through_the_settings_store()
    {
        var folder = Path.Combine(_root, "settings");
        Directory.CreateDirectory(folder);

        var database = new Database(new AppPaths(folder));
        await database.MigrateAsync(CancellationToken.None);
        var store = new SettingsStore(database);

        var fresh = await store.LoadAsync(CancellationToken.None);
        fresh.Verification.SemanticReverseCap.Should().Be(SemanticSettings.Default.ReverseCap);

        fresh.Verification.SemanticReverseCap = 3;
        await store.SaveAsync(fresh, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Verification.SemanticReverseCap.Should().Be(3);

        fresh.Verification.SemanticReverseCap = 0;
        await store.SaveAsync(fresh, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Verification.SemanticReverseCap.Should().Be(0);

        fresh.Verification.SemanticReverseCap = -4;
        await store.SaveAsync(fresh, CancellationToken.None);
        (await store.LoadAsync(CancellationToken.None)).Verification.SemanticReverseCap.Should().Be(0);

        SemanticRuntime.SettingsFor(new VerificationSettings { SemanticReverseCap = 7 }).ReverseCap.Should().Be(7);
        SemanticRuntime.SettingsFor(new VerificationSettings { SemanticReverseCap = 0 }).ReverseCap.Should().Be(0);
        SemanticRuntime.SettingsFor(new VerificationSettings { SemanticReverseCap = 0 }).SpanCap.Should().Be(SemanticSettings.Default.SpanCap);
    }

    [Fact]
    public void A_reverse_cap_of_zero_makes_no_reverse_call_while_similarity_still_runs()
    {
        var (services, backend, reverse) = SemanticFixtures.Services(reverseCap: 0);

        services.ReverseEnabled.Should().BeFalse();
        services.Reverse.Available.Should().BeFalse();
        services.Reverse.UnavailableReason.Should().Be(CappedReverseTranslator.DisabledReason);
        services.Settings.SpanCap.Should().Be(SemanticSettings.Default.SpanCap);

        var context = SemanticFixtures.Context(Source, Different);
        SemanticPorts.Attach(context, services);
        var decision = EscalationGate.Admit(context, [SemanticFixtures.Flag(context, 0), SemanticFixtures.Flag(context, 1)], services.Settings.SpanCap);

        decision.Admitted.Should().HaveCount(2);

        var similarity = CheckRegistry.Default.Find(CheckId.Semantics.EmbeddingSimilarity)!.Run(context);
        similarity.Should().HaveCount(2);
        backend.Calls.Should().BeGreaterThan(0);

        CheckRegistry.Default.Find(CheckId.Semantics.ReverseTranslation)!.Run(context).Should().BeEmpty();
        CheckRegistry.Default.Find(CheckId.Semantics.ReverseComparison)!.Run(context).Should().BeEmpty();
        reverse.Calls.Should().Be(0);

        new ReverseTranslationCheck().SkipReason(context).Should().Contain(CappedReverseTranslator.DisabledReason);
        new ReverseComparisonCheck().SkipReason(context).Should().Contain(CappedReverseTranslator.DisabledReason);
        new EmbeddingSimilarityCheck().SkipReason(context).Should().BeNull();
    }

    [Fact]
    public void Reverse_calls_reach_the_gate_result_and_the_entry_counters()
    {
        var (services, _, reverse) = SemanticFixtures.Services();
        var pipeline = new VerificationPipeline(null, new VerificationSettings()) { Semantics = _ => services };
        var context = pipeline.BuildContext(string.Join('\n', Source), string.Join('\n', Different), Traces(Source, Different), "en", "cs");

        context.Should().NotBeNull();

        var result = pipeline.RunGate(context!, [SemanticFixtures.Flag(context!, 0)]);

        result.Escalation.Should().NotBeNull();
        result.Escalation!.Flagged.Should().Be(1);
        result.Escalation.Admitted.Should().Be(1);
        result.Escalation.Refused.Should().Be(0);
        result.Escalation.Cap.Should().Be(SemanticSettings.Default.SpanCap);
        result.Escalation.ReverseCap.Should().Be(SemanticSettings.Default.ReverseCap);
        result.Escalation.ReverseCalls.Should().Be(1);
        result.Escalation.ReverseCalls.Should().Be(reverse.Calls);
        result.Escalation.ReverseTokens.Should().Be(DictionaryReverseTranslator.TokensPerCall);
        result.Escalation.ReverseDurationMs.Should().Be((int)DictionaryReverseTranslator.DurationPerCall.TotalMilliseconds);
        result.Escalation.Text.Should().Be("1 of 1 flagged spans re-checked, cap 24, 1 of at most 24 reverse translation call(s), 5 token(s)");
        result.Checks.Should().Contain(c => c.CheckId == CheckId.Semantics.ReverseTranslation && c.State == GateCheckState.Ran);
        GateRunReport.Render(result).Should().Contain("escalation 1 of 1 flagged spans re-checked");

        var verification = new Core.Verification.VerificationResult { Executed = true, Gate = result };
        var outcome = new TranslationOutcome("Pilník je na disku.", 12, TimeSpan.FromMilliseconds(300)) { Verification = verification }.CountingReverseCalls();

        outcome.GeneratedTokens.Should().Be(12 + DictionaryReverseTranslator.TokensPerCall);
        outcome.Duration.Should().Be(TimeSpan.FromMilliseconds(340));

        var untouched = new TranslationOutcome("x", 12, TimeSpan.FromMilliseconds(300)).CountingReverseCalls();
        untouched.GeneratedTokens.Should().Be(12);
    }

    [Fact]
    public void Each_skip_reason_reaches_the_gate_status_in_its_situation()
    {
        var modelsFolder = Path.Combine(_root, "empty-models");
        Directory.CreateDirectory(modelsFolder);

        var missingModel = new SemanticServices(
            EmbeddingModelStore.Host(modelsFolder),
            SemanticFixtures.Services().Reverse);
        var absent = Statuses(missingModel, flag: true);
        var expectedMissing = EmbeddingModelStore.MissingReason(modelsFolder);

        absent[CheckId.Semantics.EmbeddingSimilarity].Reason.Should().Contain(expectedMissing);
        absent[CheckId.Semantics.ReverseComparison].Reason.Should().Contain(expectedMissing);
        absent[CheckId.Semantics.ReverseTranslation].State.Should().Be(GateCheckState.Ran);

        var (fixture, _, _) = SemanticFixtures.Services();
        var deadSession = new SemanticServices(fixture.Embeddings, new UnavailableReverseTranslator("the inference session is not alive"));
        var dead = Statuses(deadSession, flag: true);

        dead[CheckId.Semantics.EmbeddingSimilarity].State.Should().Be(GateCheckState.Ran);
        dead[CheckId.Semantics.ReverseTranslation].Reason.Should().Contain("the inference session is not alive");
        dead[CheckId.Semantics.ReverseComparison].Reason.Should().Contain("the inference session is not alive");

        var (working, _, _) = SemanticFixtures.Services();
        var quiet = Statuses(working, flag: false);

        foreach (var checkId in new[] { CheckId.Semantics.EmbeddingSimilarity, CheckId.Semantics.ReverseTranslation, CheckId.Semantics.ReverseComparison })
        {
            quiet[checkId].State.Should().Be(GateCheckState.Skipped);
            quiet[checkId].Reason.Should().Be(SemanticCheck.NothingEscalatedReason);
        }

        var summary = Runtime.Agents.VerificationSummary.From(new Core.Verification.VerificationResult { Executed = true, Gate = Run(working, flag: false) });
        summary.Skipped.Should().Contain(s => s.CheckId == CheckId.Semantics.ReverseTranslation && s.Reason == SemanticCheck.NothingEscalatedReason);
        summary.Escalation.Should().Contain("0 of 0 flagged spans re-checked");
    }

    [Fact]
    public void The_reverse_cap_stops_calls_at_the_cap_without_touching_admission()
    {
        var (services, _, reverse) = SemanticFixtures.Services(reverseCap: 1);
        var context = SemanticFixtures.Context(Source, Different);
        SemanticPorts.Attach(context, services);

        var decision = EscalationGate.Admit(context, [SemanticFixtures.Flag(context, 0), SemanticFixtures.Flag(context, 1)], services.Settings.SpanCap);
        decision.Admitted.Should().HaveCount(2);

        var findings = CheckRegistry.Default.Find(CheckId.Semantics.ReverseTranslation)!.Run(context);

        findings.Should().ContainSingle();
        reverse.Calls.Should().Be(1);
        services.Reverse.Available.Should().BeFalse();
        services.Reverse.UnavailableReason.Should().Contain("cap of 1 reached");
        CheckRegistry.Default.Find(CheckId.Semantics.EmbeddingSimilarity)!.Run(context).Should().HaveCount(2);
    }

    private static Dictionary<string, GateCheckStatus> Statuses(SemanticServices services, bool flag) =>
        Run(services, flag).Checks
            .Where(c => c.Category == CheckId.Semantics.Category)
            .ToDictionary(c => c.CheckId, c => c, StringComparer.Ordinal);

    private static GateRunResult Run(SemanticServices services, bool flag)
    {
        var pipeline = new VerificationPipeline(null, new VerificationSettings()) { Semantics = _ => services };
        var context = pipeline.BuildContext(string.Join('\n', Source), string.Join('\n', Different), Traces(Source, Different), "en", "cs")!;

        return pipeline.RunGate(context, flag ? [SemanticFixtures.Flag(context, 0)] : []);
    }

    private static List<SegmentTrace> Traces(IReadOnlyList<string> source, IReadOnlyList<string> target)
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

        return traces;
    }
}
