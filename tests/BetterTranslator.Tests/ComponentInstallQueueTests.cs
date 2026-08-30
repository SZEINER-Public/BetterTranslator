using System.IO;
using System.Linq;
using System.Net.Http;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The queue every component reaches the network through.
///
/// It exists because a first install listed components as already present and
/// then fetched them anyway: the tick that said "installed" and the check that
/// decided whether to transfer were two different questions asked at two
/// different moments. One resolve now settles both, before anything is queued.
/// </summary>
public sealed class ComponentInstallQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-queue-" + Guid.NewGuid().ToString("N"));

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

    private static byte[] Gguf(int totalBytes)
    {
        var body = new byte[totalBytes];
        body[0] = 0x47; body[1] = 0x47; body[2] = 0x55; body[3] = 0x46;
        body[4] = 3;

        for (var i = 8; i < totalBytes; i++)
        {
            body[i] = (byte)(i % 251);
        }

        return body;
    }

    private (InstallPaths Paths, ModelResolver Resolver) Store(string? leaf = null)
    {
        var paths = new InstallPaths(new AppPaths(leaf is null ? _root : Path.Combine(_root, leaf)));
        paths.EnsureCreated();

        return (paths, new ModelResolver(paths, new ModelLibrary()));
    }

    private static ModelComponent Component(Uri url, long size = 4096) => new()
    {
        Id = "queued-model",
        Name = "Queued Model",
        Kind = ComponentKind.Model,
        Summary = "A model used by the tests",
        SizeBytes = size,
        Version = "1",
        ArtifactFileName = "queued-model.gguf",
        DownloadUrl = url,
    };

    [Fact]
    public async Task AnAlreadyInstalledComponentIsNeverEnqueued()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Gguf(4096) };
        });

        var (paths, resolver) = Store();
        var component = Component(new Uri(server.BaseAddress, "/model"));

        // Exactly what the installer writes: the artifact at the destination, at
        // the length the catalogue declares.
        await File.WriteAllBytesAsync(paths.PathFor(component), Gguf(4096));

        using var client = server.CreateClient();
        var queue = new ComponentInstallQueue(new DownloadManager(client, paths, resolver), resolver);

        queue.Resolve(component).Reason.Should().Be(PresenceReason.InstalledHere);

        var result = await queue.EnqueueAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        queue.IsPending(component.Id).Should().BeFalse("nothing was ever queued for it");
        served.Should().Be(0, "the bytes are already at the destination");
    }

    [Fact]
    public async Task ATruncatedComponentIsDownloadedAgain()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Gguf(4096) };
        });

        var (paths, resolver) = Store();
        var component = Component(new Uri(server.BaseAddress, "/model"));

        // A file at the right path under the right name, and short. Treating any
        // existing file as installed is the regression this guards.
        await File.WriteAllBytesAsync(paths.PathFor(component), Gguf(1024));

        using var client = server.CreateClient();
        var queue = new ComponentInstallQueue(new DownloadManager(client, paths, resolver), resolver);

        queue.Resolve(component).Reason.Should().Be(PresenceReason.WrongLength);

        var result = await queue.EnqueueAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        served.Should().Be(1, "a partial copy is not an install");
        new FileInfo(paths.PathFor(component)).Length.Should().Be(4096);
    }

    [Fact]
    public async Task ACompanionOnDiskAtTheWrongLengthIsFetchedAgain()
    {
        // The same rule one level down. A cuBLAS library half written is a
        // runtime that will not load, and existence alone said it was there.
        var (paths, _) = Store();

        var companion = new CompanionArtifact
        {
            FileName = "queued-companion.dll",
            SizeBytes = 4096,
            Reason = "needed beside the runtime",
        };

        await File.WriteAllBytesAsync(paths.PathFor(companion), new byte[16]);

        ComponentInstallState.ArtifactMatches(paths.PathFor(companion), companion.SizeBytes)
            .Should().BeFalse("16 bytes is not the 4096 the catalogue declares");

        await File.WriteAllBytesAsync(paths.PathFor(companion), new byte[4096]);

        ComponentInstallState.ArtifactMatches(paths.PathFor(companion), companion.SizeBytes)
            .Should().BeTrue();
    }

    [Fact]
    public async Task TwoConcurrentEnqueuesForOneComponentProduceOneDownload()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);

            // Long enough that the second call arrives while the first is still
            // moving, which is the state the rejection exists for.
            return new StubResponse { Body = Gguf(4096), Delay = TimeSpan.FromMilliseconds(250) };
        });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var queue = new ComponentInstallQueue(new DownloadManager(client, paths, resolver), resolver);
        var component = Component(new Uri(server.BaseAddress, "/model"));

        var both = await Task.WhenAll(
            queue.EnqueueAsync(component, null, CancellationToken.None),
            queue.EnqueueAsync(component, null, CancellationToken.None));

        both.Should().AllSatisfy(r => r.State.Should().Be(DownloadState.Installed));
        served.Should().Be(1, "the second enqueue joins the transfer already running");
        queue.IsPending(component.Id).Should().BeFalse("the entry is released once the transfer ends");
    }

    [Fact]
    public async Task ARowDisplayedAsInstalledIsNotFetchedInTheSameRun()
    {
        // The defect as the user met it, driven through the real installer: a
        // row that reads "installed" must not be one that downloads.
        var recorder = new RecordingHandler();
        var paths = new InstallPaths(new AppPaths(Path.Combine(_root, "installer")));
        paths.EnsureCreated();

        var installer = new FirstRunViewModel(paths, new HttpClient(recorder))
        {
            CustomModelLink = "http://127.0.0.1:9/queued-custom.gguf",
        };

        installer.AddCustomModelCommand.Execute(null);
        installer.LinkRejection.Should().BeNull();

        var row = installer.Items[^1];
        await File.WriteAllBytesAsync(paths.PathFor(row.Component), Gguf(4096));

        installer.RefreshPresence();
        row.IsInstalledHere.Should().BeTrue("the artifact is at the destination");
        row.IsSelected.Should().BeTrue("a row at the destination is ticked and locked");
        row.CanDeselect.Should().BeFalse();

        foreach (var item in installer.Items)
        {
            item.IsSelected = item == row;
        }

        installer.SelectedBytes.Should().Be(0, "nothing will be fetched");

        await installer.InstallCommand.ExecuteAsync(null);

        row.State.Should().Be(DownloadState.Installed);
        recorder.Requests.Should().BeEmpty("a component displayed as installed opened no socket");
        installer.Stage.Should().Be(FirstRunStage.ChooseWhereToStart);
    }

    [Fact]
    public async Task ARuntimeBesideTheExecutableWithoutItsLibrariesIsNotCalledInstalled()
    {
        // Captured from the reproduction log: the runtime is found beside the
        // executable, that branch skipped the dependency rule, and the row read
        // "is installed" while the run behind it fetched the libraries.
        var (paths, resolver) = Store("beside");

        var companion = new CompanionArtifact
        {
            FileName = "besideprobe-companion.dll",
            SizeBytes = 4096,
            Reason = "imported by the runtime",
        };

        var runtime = new ModelComponent
        {
            Id = "besideprobe",
            Name = "Beside Probe",
            Kind = ComponentKind.Runtime,
            Summary = "a runtime used by the tests",
            SizeBytes = 4096,
            Version = "1",
            ArtifactFileName = "besideprobe.dll",
            Companions = [companion],
        };

        var beside = Path.Combine(AppContext.BaseDirectory, runtime.FileName);
        await File.WriteAllBytesAsync(beside, new byte[4096]);

        try
        {
            paths.IsDefault.Should().BeTrue();

            var presence = resolver.Resolve(runtime);

            presence.Reason.Should().Be(PresenceReason.MissingDependencies);
            presence.Missing.Should().ContainSingle().Which.Should().Be(companion.FileName);

            var row = new InstallItemViewModel(runtime) { Presence = presence };
            row.IsInstalledHere.Should().BeFalse("it is on disk and cannot load");
            row.IsIncomplete.Should().BeTrue();
        }
        finally
        {
            File.Delete(beside);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
