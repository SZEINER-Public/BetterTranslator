using System.IO;
using System.Linq;
using System.Text;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The failure modes a download manager exists to survive. Every one runs
/// offline against StubHttpServer in milliseconds: the point is that a
/// half-written or wrong file can never be promoted, and none of these needs a
/// network to prove.
/// </summary>
public sealed class DownloadIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-dl-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>A real GGUF header, so a body can pass the structural check.</summary>
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

    private (InstallPaths Paths, ModelResolver Resolver) Store()
    {
        var paths = new InstallPaths(new AppPaths(_root));
        paths.EnsureCreated();
        return (paths, new ModelResolver(paths, new ModelLibrary()));
    }

    private static ModelComponent Component(Uri url, long size) => new()
    {
        Id = "testmodel",
        Name = "Test Model",
        Kind = ComponentKind.Model,
        Summary = "A model used by the tests",
        SizeBytes = size,
        Version = "1",
        ArtifactFileName = "test-model.gguf",
        DownloadUrl = url,
    };

    [Fact]
    public async Task AnHtmlBodyOnA200IsAQuotaPageNotAModel()
    {
        // Drive answers an exhausted quota with status 200 and an HTML page, so
        // the status code alone never proves the bytes are a model.
        using var server = new StubHttpServer(_ => new StubResponse
        {
            Status = 200,
            ContentType = "text/html",
            Body = Encoding.UTF8.GetBytes("<html><body>Quota exceeded</body></html>"),
        });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        result.Failure.Should().Contain("quota");
        File.Exists(Path.Combine(paths.ModelsFolder, "test-model.gguf")).Should().BeFalse();
    }

    [Fact]
    public async Task ATruncatedBodyIsNeverPromoted()
    {
        // Content-Length says 4096; the server sends 2048 and closes.
        using var server = new StubHttpServer(_ => new StubResponse
        {
            Body = Gguf(2048),
            DeclaredLength = 4096,
        });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        File.Exists(Path.Combine(paths.ModelsFolder, "test-model.gguf"))
            .Should().BeFalse("a short file must never look installed");
    }

    [Fact]
    public async Task ARightSizedFileThatIsNotAGgufIsRejected()
    {
        // The length check alone would pass this: an error page that happens to
        // be the expected size. The structural check is what catches it.
        using var server = new StubHttpServer(_ => new StubResponse { Body = new byte[4096] });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Failed);
        result.Failure.Should().Contain("not a GGUF");
    }

    [Fact]
    public async Task AGoodDownloadIsPromotedAndHashed()
    {
        using var server = new StubHttpServer(_ => new StubResponse { Body = Gguf(4096) });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        result.Sha256.Should().NotBeNullOrEmpty("hashing runs as the bytes stream past");
        File.Exists(Path.Combine(paths.ModelsFolder, "test-model.gguf")).Should().BeTrue();
        File.Exists(Path.Combine(paths.ModelsFolder, "test-model.gguf.part")).Should().BeFalse();
    }

    [Fact]
    public async Task AnExistingPartIsResumedRatherThanRestarted()
    {
        var full = Gguf(4096);
        long? servedFrom = null;

        using var server = new StubHttpServer(r =>
        {
            servedFrom = r.RangeFrom;

            if (r.RangeFrom is { } from)
            {
                return new StubResponse
                {
                    Status = 206,
                    Body = full[(int)from..],
                    Headers = { ["Content-Range"] = $"bytes {from}-{full.Length - 1}/{full.Length}" },
                };
            }

            return new StubResponse { Body = full };
        });

        var (paths, resolver) = Store();

        // A previous run left the first 1500 bytes behind.
        var partial = Path.Combine(paths.ModelsFolder, "test-model.gguf.part");
        await File.WriteAllBytesAsync(partial, full[..1500]);

        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        servedFrom.Should().Be(1500, "the resume asked for exactly what was missing");
        result.State.Should().Be(DownloadState.Installed);

        var written = await File.ReadAllBytesAsync(Path.Combine(paths.ModelsFolder, "test-model.gguf"));
        written.Should().BeEquivalentTo(full, "the resumed halves join back into the original");
    }

    [Fact]
    public async Task AServerThatIgnoresRangeStartsOverRatherThanAppending()
    {
        var full = Gguf(4096);

        // Answers 200 with the whole file even though Range was sent. Appending
        // here would produce a file of 1500 + 4096 bytes.
        using var server = new StubHttpServer(_ => new StubResponse { Body = full });

        var (paths, resolver) = Store();
        var partial = Path.Combine(paths.ModelsFolder, "test-model.gguf.part");
        await File.WriteAllBytesAsync(partial, full[..1500]);

        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        new FileInfo(Path.Combine(paths.ModelsFolder, "test-model.gguf")).Length.Should().Be(4096);
    }

    [Fact]
    public async Task CancellingMidFileKeepsThePartAndReportsCancelled()
    {
        using var cancel = new CancellationTokenSource();

        using var server = new StubHttpServer(_ =>
        {
            cancel.Cancel();
            return new StubResponse { Body = Gguf(4096) };
        });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, cancel.Token);

        result.State.Should().Be(DownloadState.Cancelled);
        File.Exists(Path.Combine(paths.ModelsFolder, "test-model.gguf"))
            .Should().BeFalse("a cancelled transfer never looks installed");
    }

    [Fact]
    public async Task AModelAlreadyOnDiskCostsNoRequestAtAll()
    {
        using var server = new StubHttpServer(_ => new StubResponse { Body = Gguf(4096) });

        var (paths, resolver) = Store();

        // Already installed, at the exact expected length.
        await File.WriteAllBytesAsync(Path.Combine(paths.ModelsFolder, "test-model.gguf"), Gguf(4096));

        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(
            Component(new Uri(server.BaseAddress, "/model"), 4096), null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        server.Requests.Should().BeEmpty("the presence check runs before any socket opens");
        result.Detail.Should().Contain("installed");
    }

    [Fact]
    public async Task AFileOfTheWrongLengthIsNotPassedOffAsTheRequestedOne()
    {
        var (paths, resolver) = Store();

        // Same name, different build. Accepting it would load silently and
        // translate badly, which is far harder to diagnose than a download.
        await File.WriteAllBytesAsync(Path.Combine(paths.ModelsFolder, "test-model.gguf"), Gguf(2048));

        var presence = resolver.Resolve(Component(new Uri("http://localhost/model"), 4096));

        presence.Reason.Should().Be(PresenceReason.WrongLength);
        presence.IsSatisfied.Should().BeFalse();
        presence.Explain("Test Model").Should().Contain("different build");
    }

    [Fact]
    public async Task AnArtifactFoundElsewhereIsCopiedInRatherThanFetchedAgain()
    {
        // The defect: a row ticked for a model that exists in another tool's
        // store was reported Installed without anything being written to the
        // chosen folder. The screen had priced it at gigabytes, so Install
        // appeared to do nothing and jumped straight to the next step.
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Gguf(4096) };
        });

        var (paths, resolver) = Store();
        var component = Component(new Uri("http://localhost/model"), 4096);

        // A copy on the machine, but not at the path this install writes to.
        // Nested under the models folder because the library scan recurses, so
        // this is genuinely "found elsewhere" without the test going anywhere
        // near the real shared store.
        var elsewhere = Path.Combine(paths.ModelsFolder, "another-tool");
        Directory.CreateDirectory(elsewhere);

        var body = Gguf(4096);
        await File.WriteAllBytesAsync(Path.Combine(elsewhere, component.FileName), body);

        resolver.Resolve(component).Reason.Should().Be(PresenceReason.InstalledElsewhere,
            "the fixture must actually reproduce the state that caused the defect");

        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);

        var result = await manager.DownloadAsync(component, null, CancellationToken.None);

        result.State.Should().Be(DownloadState.Installed);
        served.Should().Be(0, "the bytes were already on the machine; nothing should have been fetched");

        var destination = paths.PathFor(component);
        File.Exists(destination).Should().BeTrue("the row was ticked to put the file in the chosen folder");
        (await File.ReadAllBytesAsync(destination)).Should().Equal(body);
        File.Exists(destination + ".part").Should().BeFalse("the copy is promoted, not left as a .part");
    }

    [Fact]
    public void ChoosingAFolderOfYourOwnUnticksWhatIsNotInIt()
    {
        // The runtime lives beside the executable, which is where it must be to
        // load at all. On the default folder that counts as installed. It does
        // not follow the installer to a folder of the reader's choosing, and a
        // row that stayed ticked there would promise a file into a folder that
        // will not have it.
        var paths = new InstallPaths(new AppPaths(_root));
        paths.EnsureCreated();

        var resolver = new ModelResolver(paths, new ModelLibrary());
        var runtime = ComponentCatalog
            .RuntimesFor(new HardwareReport(false, false, false, false))
            .Single(r => r.Id == "betterruntime-cpu");

        paths.IsDefault.Should().BeTrue();
        var onDefault = resolver.Resolve(runtime);

        paths.UseFolder(Path.Combine(_root, "somewhere-else"));
        Directory.CreateDirectory(paths.ModelsFolder);

        var onCustom = resolver.Resolve(runtime);

        // Whether the DLL is beside the test host is a property of the machine,
        // not of the code: the solution has to build and test where no native
        // build exists at all. So the invariant is the transition, checked both
        // ways, rather than a state that needs the developer to have built it.
        if (onDefault.Reason == PresenceReason.InstalledHere)
        {
            onCustom.Reason.Should().Be(PresenceReason.InstalledElsewhere,
                "it exists, but not in the folder being installed to");
            onCustom.IsSatisfied.Should().BeTrue("the mark still shows; only the tick goes away");
            onCustom.Explain("BetterRuntime (CPU)").Should().Contain("not in the folder chosen above");
        }
        else
        {
            onDefault.Reason.Should().Be(PresenceReason.Missing, "there is no native build on this machine");
            onCustom.Reason.Should().Be(PresenceReason.Missing);
        }

        // Holds however the machine is set up: a folder of your own never
        // reports a file that is somewhere else as being in it.
        onCustom.Reason.Should().NotBe(PresenceReason.InstalledHere);
    }

    [Fact]
    public async Task TwoCallersRacingForOneArtifactProduceOneDownload()
    {
        var served = 0;

        using var server = new StubHttpServer(_ =>
        {
            Interlocked.Increment(ref served);
            return new StubResponse { Body = Gguf(4096) };
        });

        var (paths, resolver) = Store();
        using var client = server.CreateClient();
        var manager = new DownloadManager(client, paths, resolver);
        var component = Component(new Uri(server.BaseAddress, "/model"), 4096);

        var both = await Task.WhenAll(
            manager.DownloadAsync(component, null, CancellationToken.None),
            manager.DownloadAsync(component, null, CancellationToken.None));

        both.Should().AllSatisfy(r => r.State.Should().Be(DownloadState.Installed));
        served.Should().Be(1, "the loser of the race finds the file already present and fetches nothing");
    }
}
