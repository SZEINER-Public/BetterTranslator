using System.IO;
using System.Threading.Tasks;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The runtime panel behind the play/pause button.
///
/// Nothing here loads a model: what is being checked is that the controls offer
/// what the state actually allows, and that the panel says which state that is
/// rather than leaving it to a coloured dot.
/// </summary>
public sealed class RuntimeStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-runtime", Guid.NewGuid().ToString("N"));
    private readonly LocalTranslator _translator = new();

    public RuntimeStatusTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _translator.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RuntimeStatusViewModel Status(string modelPath, RuntimeBackend backend = RuntimeBackend.Cpu) =>
        new(_translator, () => modelPath, () => backend);

    private string AModelFile()
    {
        var path = Path.Combine(_root, "a-model.gguf");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void WithNoModelThereIsNothingToStartAndTheReasonIsStated()
    {
        var status = Status(string.Empty);

        status.HasModel.Should().BeFalse();
        status.CanStart.Should().BeFalse("there is nothing to load");
        status.CanPause.Should().BeFalse();
        status.CanUnload.Should().BeFalse();
        status.StateLabel.Should().Be("No model");
        status.StateDetail.Should().Contain("Models and runtimes", "the panel has to say where to get one");
    }

    [Fact]
    public void AModelOnDiskCanBeStartedAndSaysItIsNotLoadedYet()
    {
        var status = Status(AModelFile());

        status.HasModel.Should().BeTrue();
        status.CanStart.Should().BeTrue();
        status.CanUnload.Should().BeFalse("nothing is loaded to unload");
        status.StateLabel.Should().Be("Not loaded");
        status.ModelLabel.Should().Be("a-model", "the model that would load is named before it has");
    }

    [Fact]
    public void TheBackendIsNamedRatherThanLeftToTheIcon()
    {
        Status(AModelFile(), RuntimeBackend.Cpu).BackendLabel.Should().Be("CPU");
        Status(AModelFile(), RuntimeBackend.Vulkan).BackendLabel.Should().Be("AMD Vulkan");
        Status(AModelFile(), RuntimeBackend.Cuda).BackendLabel.Should().Be("NVIDIA CUDA");
    }

    [Fact]
    public async Task StartingAModelThatIsNotAModelFailsAndSaysWhy()
    {
        // Three bytes are not a GGUF. The point is that the failure surfaces in
        // the panel rather than being discovered on the next send.
        var status = Status(AModelFile());

        await status.StartCommand.ExecuteAsync(null);

        status.IsPanelOpen.Should().BeTrue("pressing start and being shown nothing reads as a dead button");
        status.HasFailed.Should().BeTrue();
        status.StateLabel.Should().Be("Could not start");
        status.StateDetail.Should().NotBeNullOrWhiteSpace("the reason from the runtime is what the panel is for");
    }

    [Fact]
    public async Task UnloadingWhenNothingIsLoadedIsHarmless()
    {
        var status = Status(AModelFile());

        await status.UnloadCommand.ExecuteAsync(null);

        status.State.Should().Be(TranslatorState.Idle);
        status.CanUnload.Should().BeFalse();
    }

    [Fact]
    public void PausingAndUnloadingAreDifferentThings()
    {
        // Pause abandons the answer being written and leaves the model where it
        // is; unload gives the memory back. Conflating them was the first cut of
        // this panel, and it meant every stop paid the load again on the next
        // send.
        var status = Status(AModelFile());

        status.IsGenerating.Should().BeFalse();
        status.CanPause.Should().BeFalse("there is no run to stop");

        // Neither throws when there is nothing to act on.
        status.PauseCommand.Execute(null);

        status.State.Should().Be(TranslatorState.Idle, "pausing must never unload");
    }

    [Fact]
    public void TheLastRunIsOnlyShownOnceThereHasBeenOne()
    {
        var status = Status(AModelFile());

        status.HasLastRun.Should().BeFalse();

        status.LastRun = "9 tokens in 0.4 s (22 tok/s)";
        status.HasLastRun.Should().BeTrue();
    }

    [Fact]
    public void TogglingThePanelIsWhatOpensAndClosesIt()
    {
        var status = Status(AModelFile());

        status.IsPanelOpen.Should().BeFalse();

        status.TogglePanelCommand.Execute(null);
        status.IsPanelOpen.Should().BeTrue();

        status.TogglePanelCommand.Execute(null);
        status.IsPanelOpen.Should().BeFalse();
    }
}
