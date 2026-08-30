using System.IO;
using System.Linq;
using System.Text;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class AccelerationProvisioningProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-accel-" + Guid.NewGuid().ToString("N"));
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "bt-cuda-attempts.log");
    private readonly StringBuilder _log = new();

    public void Dispose()
    {
        File.AppendAllText(_logPath, _log.ToString());

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

    private void Line(string text) => _log.AppendLine(text);

    private static readonly HardwareReport Nvidia =
        new(HasNvidia: true, HasAmd: false, CanUseVulkan: false, CanUseCuda: true);

    private static ModelComponent Cuda =>
        ComponentCatalog.RuntimesFor(Nvidia).Single(c => c.Id == "betterruntime-cuda");

    private static byte[] Pe(int total)
    {
        var body = new byte[total];
        body[0] = 0x4D;
        body[1] = 0x5A;
        return body;
    }

    private static void PlaceCompletedInstall(string folder)
    {
        var cuda = Cuda;

        Directory.CreateDirectory(folder);
        SparseArtifact.Write(Path.Combine(folder, cuda.FileName), cuda.SizeBytes, [0x4D, 0x5A]);

        foreach (var companion in cuda.Companions)
        {
            SparseArtifact.Write(Path.Combine(folder, companion.FileName), companion.SizeBytes, [0x4D, 0x5A]);
        }
    }

    private static bool Offers(string folder)
    {
        var option = new BackendCatalog(folder).Options(Nvidia).Single(o => o.Backend == RuntimeBackend.Cuda);

        return option.IsSupported && !option.IsInstalled;
    }

    [Fact]
    public void CaptureOrderedCudaAttemptLog()
    {
        DefaultFolderScenario();
        ChosenFolderScenario();
        TwoQueuesScenario();

        Assert.True(true);
    }

    private void DefaultFolderScenario()
    {
        var root = Path.Combine(_root, "default-folder");
        var paths = new InstallPaths(new AppPaths(root));
        paths.EnsureCreated();

        Line("=== scenario A: installed into the default folder, the one the loader was told about at startup ===");
        Line($"loader search folder : {paths.ModelsFolder}");

        PlaceCompletedInstall(paths.ModelsFolder);

        Line($"install wrote        : {string.Join(", ", Directory.GetFiles(paths.ModelsFolder).Select(Path.GetFileName))}");
        Line($"verification         : installed={new BackendCatalog(paths.ModelsFolder).IsInstalled(RuntimeBackend.Cuda)}");
        Line($"Runtime card offers  : CanFetch={Offers(paths.ModelsFolder)}");
        Line(string.Empty);
    }

    private void ChosenFolderScenario()
    {
        var root = Path.Combine(_root, "chosen-folder");
        var paths = new InstallPaths(new AppPaths(root));
        paths.EnsureCreated();

        var registeredAtStartup = paths.ModelsFolder;
        var chosen = Path.Combine(root, "my-models");

        Line("=== scenario B: installed into a folder the reader chose, which the loader was never told about ===");
        Line($"loader search folder : {registeredAtStartup} (registered once at startup, from the default)");
        Line($"chosen models folder : {chosen}");

        paths.UseFolder(chosen);
        PlaceCompletedInstall(chosen);

        var resolver = new ModelResolver(paths, new ModelLibrary());

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Line($"attempt {attempt} trigger      : Runtime card CanFetch={Offers(registeredAtStartup)}, so Get is pressed again");
            Line($"attempt {attempt} probe        : the installer resolves {resolver.Resolve(Cuda).Reason}");
            Line($"attempt {attempt} install end  : nothing fetched, the artifacts are already at the destination");
            Line($"attempt {attempt} verification : loader search folder says installed="
                + $"{new BackendCatalog(registeredAtStartup).IsInstalled(RuntimeBackend.Cuda)}, "
                + $"folder actually written says installed={new BackendCatalog(chosen).IsInstalled(RuntimeBackend.Cuda)}");
        }

        Line(string.Empty);
        Line("--- the same state, through the verifier the install now runs ---");

        var verified = new ComponentInstallVerifier(paths)
            .VerifyAsync(Cuda, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Line($"verifier             : passed={verified.Passed}, {verified.Detail}");
        Line($"loader search paths  : now include the chosen folder={BackendCatalog.SearchPaths.Contains(chosen)}");
        Line($"Runtime card offers  : CanFetch={Offers(chosen)}");
        Line(string.Empty);
    }

    private void TwoQueuesScenario()
    {
        Line("=== scenario C: one component id and version, two install queues, as two openings of the panel produce ===");

        var served = 0;
        var gate = new SemaphoreSlim(0, 2);

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            gate.Release();

            return new StubResponse { Body = Pe(4096), Delay = TimeSpan.FromMilliseconds(400) };
        });

        var paths = new InstallPaths(new AppPaths(Path.Combine(_root, "two-queues")));
        paths.EnsureCreated();

        var component = new ModelComponent
        {
            Id = "betterruntime-cuda",
            Name = "BetterRuntime (NVIDIA CUDA)",
            Kind = ComponentKind.Runtime,
            Summary = "the shared acceleration component",
            SizeBytes = 4096,
            Version = "b4120",
            ArtifactFileName = "accel-probe-runtime.dll",
            DownloadUrl = new Uri(server.BaseAddress, "/cuda"),
        };

        var resolver = new ModelResolver(paths, new ModelLibrary());
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var first = new ComponentInstallQueue(manager, resolver, paths);
        var second = new ComponentInstallQueue(manager, resolver, paths);

        Line("trigger 1            : the first panel enqueues betterruntime-cuda@b4120");
        Line("trigger 2            : a second panel, built after the first was released, enqueues the same id");

        var running = Task.WhenAll(
            first.EnqueueAsync(component, null, CancellationToken.None),
            second.EnqueueAsync(component, null, CancellationToken.None));

        gate.Wait(TimeSpan.FromSeconds(5));

        Line($"in flight            : first queue pending={first.IsPending(component)}, "
            + $"second queue pending={second.IsPending(component)}");

        running.GetAwaiter().GetResult();

        Line($"fetches              : {served} request(s) reached the server");
        Line("note                 : the queues do not know about each other. What kept the socket count down is");
        Line("                       ArtifactLock, a lock file in the temp folder whose failure path returns a lock");
        Line("                       holding nothing, so the guarantee is a coincidence rather than a contract.");
        Line(string.Empty);
    }
}
