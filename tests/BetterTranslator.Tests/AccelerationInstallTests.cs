using System.IO;
using System.Linq;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Installing an acceleration component once.
///
/// The reported defect was four installs of one CUDA runtime. Two things put
/// it there. The install writes into the models folder, which the reader may
/// redirect, while every surface asking whether a flavour is installed reads a
/// loader search list registered once at startup from the default folder, so a
/// completed install went on reading as missing and the Get button went on
/// offering it. And the single flight registry belonged to the panel rather
/// than to the session, so each opening brought its own.
/// </summary>
public sealed class AccelerationInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-cuda-" + Guid.NewGuid().ToString("N"));

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

    private static readonly HardwareReport Nvidia =
        new(HasNvidia: true, HasAmd: false, CanUseVulkan: false, CanUseCuda: true);

    private static byte[] Pe(int total)
    {
        var body = new byte[total];
        body[0] = 0x4D;
        body[1] = 0x5A;
        return body;
    }

    private (InstallPaths Paths, ModelResolver Resolver) Store(string leaf)
    {
        var paths = new InstallPaths(new AppPaths(Path.Combine(_root, leaf)));
        paths.EnsureCreated();

        return (paths, new ModelResolver(paths, new ModelLibrary()));
    }

    private static ModelComponent Accelerator(Uri url, string version = "b4120") => new()
    {
        Id = "betterruntime-cuda",
        Name = "BetterRuntime (NVIDIA CUDA)",
        Kind = ComponentKind.Runtime,
        Summary = "the shared acceleration component",
        SizeBytes = 4096,
        Version = version,
        ArtifactFileName = "accel-test-runtime.dll",
        DownloadUrl = url,
    };

    private sealed class CountingVerifier(bool passes) : IInstallVerifier
    {
        public int Calls { get; private set; }

        public Task<InstallVerification> VerifyAsync(ModelComponent component, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(passes
                ? InstallVerification.Ok("verified")
                : InstallVerification.Fail($"{component.Name} is not on disk where the installer put it.", [component.FileName]));
        }
    }

    private static ComponentInstallQueue Queue(
        DownloadManager downloads,
        ModelResolver resolver,
        IInstallVerifier verifier) =>
        new(downloads, resolver, verifier, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task ConcurrentRequestsForOneComponentProduceOneInstall()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Pe(4096), Delay = TimeSpan.FromMilliseconds(300) };
        });

        var (paths, resolver) = Store("concurrent");
        using var client = server.CreateClient();
        var verifier = new CountingVerifier(passes: true);
        var queue = Queue(new DownloadManager(client, paths, resolver), resolver, verifier);
        var component = Accelerator(new Uri(server.BaseAddress, "/cuda"));

        var all = await Task.WhenAll(Enumerable
            .Range(0, 4)
            .Select(_ => queue.EnqueueAsync(component, null, CancellationToken.None)));

        all.Should().AllSatisfy(r => r.State.Should().Be(DownloadState.Installed));
        served.Should().Be(1, "four requests for one component id and version are one install");
        verifier.Calls.Should().Be(1, "the install that ran is the only one there was to verify");
        queue.IsPending(component).Should().BeFalse();
    }

    [Fact]
    public async Task SeveralNvidiaDevicesProduceOneInstallOfTheSharedComponent()
    {
        // One request per device, which is what a per device enumeration would
        // produce. The runtime DLL is shared, so the count of cards changes
        // nothing about how many times it is fetched.
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Pe(4096), Delay = TimeSpan.FromMilliseconds(300) };
        });

        var (paths, resolver) = Store("devices");
        using var client = server.CreateClient();
        var queue = Queue(new DownloadManager(client, paths, resolver), resolver, new CountingVerifier(passes: true));
        var component = Accelerator(new Uri(server.BaseAddress, "/cuda"));

        string[] devices = ["GPU 0", "GPU 1", "GPU 2"];

        var all = await Task.WhenAll(devices.Select(_ => queue.EnqueueAsync(component, null, CancellationToken.None)));

        all.Should().AllSatisfy(r => r.State.Should().Be(DownloadState.Installed));
        served.Should().Be(1, "three cards share one runtime component");

        // And the catalogue itself is keyed by backend rather than by device,
        // so there is only ever one row to enqueue however many cards are in
        // the machine.
        ComponentCatalog.RuntimesFor(Nvidia)
            .Count(c => c.Id == "betterruntime-cuda")
            .Should().Be(1);
    }

    [Fact]
    public async Task ASatisfiedProbeProducesNoInstall()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Pe(4096) };
        });

        var (paths, resolver) = Store("satisfied");
        var component = Accelerator(new Uri(server.BaseAddress, "/cuda"));

        await File.WriteAllBytesAsync(paths.PathFor(component), Pe(4096));

        using var client = server.CreateClient();
        var verifier = new CountingVerifier(passes: true);
        var queue = Queue(new DownloadManager(client, paths, resolver), resolver, verifier);

        queue.Resolve(component).Reason.Should().Be(PresenceReason.InstalledHere);

        var result = await queue.EnqueueAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        served.Should().Be(0, "the component is already where the installer would put it");
        verifier.Calls.Should().Be(1, "a satisfied probe is still verified, it is just never fetched");
    }

    [Fact]
    public async Task AVerificationThatKeepsFailingStopsAtTheCeilingAndReportsIt()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Pe(4096) };
        });

        var (paths, resolver) = Store("ceiling");
        using var client = server.CreateClient();
        var verifier = new CountingVerifier(passes: false);
        var queue = Queue(new DownloadManager(client, paths, resolver), resolver, verifier);
        var component = Accelerator(new Uri(server.BaseAddress, "/cuda"));

        var result = await queue.EnqueueAsync(component, null, CancellationToken.None);

        verifier.Calls.Should().Be(
            ComponentInstallQueue.VerificationAttempts,
            "the ceiling is stated and it is where the retries stop");

        served.Should().Be(1, "a verification that fails is never answered by installing again");

        result.State.Should().Be(DownloadState.Failed);
        result.Failure.Should().NotBeNullOrEmpty("the failure is surfaced rather than swallowed");
        result.Failure.Should().Contain(ComponentInstallQueue.VerificationAttempts.ToString());
        result.Failure.Should().Contain("not a transfer problem");
    }

    [Fact]
    public async Task AProgressFollowerSeesTheVerificationFailure()
    {
        using var server = new StubHttpServer(_ => new StubResponse { Body = Pe(4096) });

        var (paths, resolver) = Store("reported");
        using var client = server.CreateClient();
        var queue = Queue(new DownloadManager(client, paths, resolver), resolver, new CountingVerifier(passes: false));
        var component = Accelerator(new Uri(server.BaseAddress, "/cuda"));

        var seen = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(seen.Add);

        await queue.EnqueueAsync(component, progress, CancellationToken.None);

        // Progress is marshalled, so the report may land after the await. What
        // matters is that a failure reaches the sink at all rather than the row
        // being left reading Installed.
        var landed = SpinWait.SpinUntil(
            () =>
            {
                lock (seen)
                {
                    return seen.Any(p => p.State == DownloadState.Failed);
                }
            },
            TimeSpan.FromSeconds(5));

        landed.Should().BeTrue("the row has to say the install did not verify");
    }

    [Fact]
    public void TheIdempotencyKeyCarriesTheVersion()
    {
        var url = new Uri("http://127.0.0.1:9/cuda");

        ComponentInstallQueue.KeyFor(Accelerator(url)).Should().Be("betterruntime-cuda@b4120");

        ComponentInstallQueue.KeyFor(Accelerator(url, "b4200"))
            .Should().NotBe(
                ComponentInstallQueue.KeyFor(Accelerator(url)),
                "a component rebuilt under the same id is a different artifact");
    }

    [Fact]
    public async Task ARebuiltVersionIsNotAnsweredByTheInstallOfTheOldOne()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Pe(4096), Delay = TimeSpan.FromMilliseconds(200) };
        });

        var (paths, resolver) = Store("versions");
        using var client = server.CreateClient();
        var queue = Queue(new DownloadManager(client, paths, resolver), resolver, new CountingVerifier(passes: true));

        var old = Accelerator(new Uri(server.BaseAddress, "/cuda"));
        var rebuilt = Accelerator(new Uri(server.BaseAddress, "/cuda"), "b4200") with
        {
            ArtifactFileName = "accel-test-runtime-b4200.dll",
        };

        await Task.WhenAll(
            queue.EnqueueAsync(old, null, CancellationToken.None),
            queue.EnqueueAsync(rebuilt, null, CancellationToken.None));

        served.Should().Be(2, "two versions are two artifacts, whatever the id says");
    }

    [Fact]
    public async Task TheVerifierProbesTheFolderTheInstallerWroteToAndTellsTheLoaderAboutIt()
    {
        // The defect, at the level it lives: the install lands in a folder the
        // reader chose and every surface asking about it reads a search list
        // that was told about the default folder once, at startup.
        var paths = new InstallPaths(new AppPaths(Path.Combine(_root, "folder")));
        paths.EnsureCreated();

        var chosen = Path.Combine(_root, "folder", "my-models");
        paths.UseFolder(chosen);
        Directory.CreateDirectory(chosen);

        var component = new ModelComponent
        {
            Id = "betterruntime-cuda",
            Name = "BetterRuntime (NVIDIA CUDA)",
            Kind = ComponentKind.Runtime,
            Summary = "the shared acceleration component",
            SizeBytes = 4096,
            Version = "b4120",
            ArtifactFileName = "accel-folder-runtime.dll",
            DownloadUrl = new Uri("http://127.0.0.1:9/cuda"),
        };

        var verifier = new ComponentInstallVerifier(paths);

        var beforeAnything = await verifier.VerifyAsync(component, CancellationToken.None);

        beforeAnything.Passed.Should().BeFalse("nothing is on disk yet");
        beforeAnything.Missing.Should().Contain(component.FileName);
        beforeAnything.Detail.Should().Contain(chosen, "the message names the folder that was actually checked");

        await File.WriteAllBytesAsync(paths.PathFor(component), Pe(4096));

        var afterInstall = await verifier.VerifyAsync(component, CancellationToken.None);

        afterInstall.Passed.Should().BeTrue();
        afterInstall.Detail.Should().Contain(chosen);

        BackendCatalog.SearchPaths.Should().Contain(
            chosen,
            "a flavour the loader cannot see is a flavour every surface reports as missing");
    }

    [Fact]
    public async Task AnIncompleteAcceleratorFailsVerificationRatherThanReadingAsInstalled()
    {
        var paths = new InstallPaths(new AppPaths(Path.Combine(_root, "incomplete")));
        paths.EnsureCreated();

        var companion = new CompanionArtifact
        {
            FileName = "accel-test-cublas.dll",
            SizeBytes = 2048,
            Reason = "imported by the runtime",
        };

        var component = new ModelComponent
        {
            Id = "betterruntime-cuda",
            Name = "BetterRuntime (NVIDIA CUDA)",
            Kind = ComponentKind.Runtime,
            Summary = "the shared acceleration component",
            SizeBytes = 4096,
            Version = "b4120",
            ArtifactFileName = "accel-incomplete-runtime.dll",
            Companions = [companion],
            DownloadUrl = new Uri("http://127.0.0.1:9/cuda"),
        };

        await File.WriteAllBytesAsync(paths.PathFor(component), Pe(4096));

        var verdict = await new ComponentInstallVerifier(paths).VerifyAsync(component, CancellationToken.None);

        verdict.Passed.Should().BeFalse("the library it imports is not beside it, so it cannot load");
        verdict.Missing.Should().ContainSingle().Which.Should().Be(companion.FileName);
    }
}
