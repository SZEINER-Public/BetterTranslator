using System.IO;
using System.Linq;
using System.Text;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Services.Restart;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Restarting after an install: what it relaunches, what it waits for, and how
/// often it is allowed to happen.
///
/// The three things that make this dangerous rather than convenient are all
/// asserted here. The shipped executable is a native bootstrap that unpacks a
/// payload and hosts the runtime in process, so the managed argv starts at the
/// unpacked assembly and relaunching from it would run a folder that is
/// rebuilt every session. A translation in flight is somebody's work. And a
/// restart with nothing behind it can fire again the moment it lands.
/// </summary>
public sealed class RestartAfterInstallTests : IDisposable
{
    private const string OuterExecutable = @"C:\Program Files\BetterTranslator\BetterTranslator.exe";
    private const string PayloadDirectory = @"C:\Users\reader\AppData\Local\BetterTranslator\runtime\1.0.7";

    private static readonly string PayloadAssembly = Path.Combine(PayloadDirectory, "BetterTranslator.dll");

    private readonly StringBuilder _log = new();

    public void Dispose() => File.AppendAllText(
        Path.Combine(Path.GetTempPath(), "bt-restart-target.log"),
        _log.ToString());

    private sealed class FakeWork(params string[] running) : IActiveWork
    {
        private readonly List<string> _running = [.. running];

        public int Cancellations { get; private set; }

        public IReadOnlyList<string> Running() => [.. _running];

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            Cancellations++;
            _running.Clear();

            return Task.CompletedTask;
        }
    }

    private sealed class FakeResource(string name, List<string> order) : IRestartResource
    {
        private readonly List<string> _order = order;

        public string Name { get; } = name;

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            _order.Add(Name);

            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public List<string> ReleaseOrder { get; } = [];

        public List<string> Log { get; } = [];

        public List<int> Exits { get; } = [];

        public RelaunchTarget? Launched { get; private set; }

        public IReadOnlyList<string> ReleasedWhenLaunched { get; private set; } = [];

        public bool LaunchSucceeds { get; set; } = true;

        public bool Enabled { get; set; } = true;

        public RestartGuard Guard { get; } = new();

        public FakeWork Work { get; init; } = new();

        public RelaunchTarget Target { get; init; } = ProcessRelaunch.Resolve(
            OuterExecutable, PayloadDirectory, PayloadAssembly, ["--from-the-shell"], @"C:\Program Files\BetterTranslator");

        public RestartService Service() => new(
            [
                new FakeResource("the model runtime and its child process", ReleaseOrder),
                new FakeResource("the MCP loopback listener and its port", ReleaseOrder),
                new FakeResource("the log file handles", ReleaseOrder),
                new FakeResource("the single instance mutex", ReleaseOrder),
            ],
            Work,
            Guard,
            () => Target,
            target =>
            {
                Launched = target;
                ReleasedWhenLaunched = [.. ReleaseOrder];

                return LaunchSucceeds;
            },
            Exits.Add,
            Log.Add,
            () => Enabled);
    }

    private static RestartRequest Request(string batchId = "batch-1", bool cancelActiveWork = false) => new()
    {
        BatchId = batchId,
        Reason = "EuroLLM is installed.",
        CancelActiveWork = cancelActiveWork,
    };

    [Fact]
    public void TheRelaunchTargetIsTheOuterExecutableAndNotTheUnpackedPayload()
    {
        var target = ProcessRelaunch.Resolve(
            OuterExecutable,
            PayloadDirectory,
            PayloadAssembly,
            ["--bt-cache-path", @"D:\cache"],
            @"C:\Program Files\BetterTranslator");

        _log.AppendLine("shipped shape: " + target.Detail);
        _log.AppendLine("shipped shape command line: " + target.CommandLine);

        target.IsResolved.Should().BeTrue();
        target.Executable.Should().Be(OuterExecutable);
        target.IsInsidePayload.Should().BeFalse("the payload folder is rebuilt every session");
        target.Executable.Should().NotStartWith(PayloadDirectory);
        target.Arguments.Should().Equal(["--bt-cache-path", @"D:\cache"],
            "the original command line goes back verbatim, bootstrap switches included");
        target.WorkingDirectory.Should().Be(@"C:\Program Files\BetterTranslator");
    }

    [Fact]
    public void ThePayloadAssemblyIsRefusedAsARelaunchTarget()
    {
        var target = ProcessRelaunch.Resolve(PayloadAssembly, PayloadDirectory, PayloadAssembly, [], PayloadDirectory);

        _log.AppendLine("payload shape: " + target.Detail);

        target.IsResolved.Should().BeFalse("a managed assembly is not the shipped executable");
        target.Detail.Should().Contain("payload");
    }

    [Fact]
    public void TheLiveResolutionNamesTheProcessExecutableRatherThanTheBaseDirectory()
    {
        var target = ProcessRelaunch.Resolve();

        _log.AppendLine("live Environment.ProcessPath   = " + (Environment.ProcessPath ?? "(none)"));
        _log.AppendLine("live AppContext.BaseDirectory  = " + AppContext.BaseDirectory);
        _log.AppendLine("live managed argv[0]           = " + Environment.GetCommandLineArgs()[0]);
        _log.AppendLine("live resolved                  = " + target.Detail);
        _log.AppendLine("live command line              = " + target.CommandLine);

        target.IsResolved.Should().BeTrue();
        target.Executable.Should().Be(Path.GetFullPath(Environment.ProcessPath!));
        target.Executable.Should().NotEndWith(".dll", "the payload assembly is never the thing to start");

        target.Arguments.Should().NotContain(
            Environment.GetCommandLineArgs()[0],
            "the managed entry assembly is not an argument of the outer executable");
    }

    [Fact]
    public async Task AQuiesceRefusesWhileATranslationIsRunning()
    {
        var harness = new Harness { Work = new FakeWork("the translation in this window") };
        var service = harness.Service();

        service.Inspect(Request()).Outcome.Should().Be(RestartOutcome.BlockedByActiveWork);

        var report = await service.RestartAsync(Request(), CancellationToken.None);

        report.Outcome.Should().Be(RestartOutcome.BlockedByActiveWork);
        report.ActiveWork.Should().ContainSingle();
        harness.Launched.Should().BeNull("nothing may start while a translation is in flight");
        harness.Exits.Should().BeEmpty();
        harness.ReleaseOrder.Should().BeEmpty("a held restart releases nothing");
        harness.Work.Cancellations.Should().Be(0, "a running translation is never killed without being asked about");
        harness.Guard.IsSpent("batch-1").Should().BeFalse("a held restart does not spend the batch");
    }

    [Fact]
    public async Task AnExplicitChoiceCancelsTheWorkAndThenRestarts()
    {
        var harness = new Harness { Work = new FakeWork("job_0001 (translate_batch)") };

        var report = await harness.Service().RestartAsync(Request(cancelActiveWork: true), CancellationToken.None);

        report.Outcome.Should().Be(RestartOutcome.Relaunched);
        harness.Work.Cancellations.Should().Be(1);
        harness.Launched.Should().NotBeNull();
    }

    [Fact]
    public async Task EveryResourceIsReleasedBeforeTheNewProcessStarts()
    {
        var harness = new Harness();

        var report = await harness.Service().RestartAsync(Request(), CancellationToken.None);

        report.Outcome.Should().Be(RestartOutcome.Relaunched);

        harness.ReleasedWhenLaunched.Should().Equal(
            [
                "the model runtime and its child process",
                "the MCP loopback listener and its port",
                "the log file handles",
                "the single instance mutex",
            ],
            "the port and the mutex have to be free before anything tries to claim them");

        harness.Exits.Should().Equal([0]);
    }

    [Fact]
    public async Task ASecondRestartWithNoCompletedInstallBetweenThemIsRefused()
    {
        var harness = new Harness();
        var service = harness.Service();

        (await service.RestartAsync(Request(), CancellationToken.None)).Outcome
            .Should().Be(RestartOutcome.Relaunched);

        var again = await service.RestartAsync(Request(), CancellationToken.None);

        again.Outcome.Should().Be(RestartOutcome.RefusedByGuard);
        again.Detail.Should().Contain("batch-1");
        harness.Exits.Should().Equal([0], "the second request never reached the relaunch");

        // A different completed install is a different batch and is allowed.
        (await service.RestartAsync(Request("batch-2"), CancellationToken.None)).Outcome
            .Should().Be(RestartOutcome.Relaunched);
    }

    [Fact]
    public async Task ARestartWithNoInstallBatchBehindItIsRefused()
    {
        var harness = new Harness();

        var report = await harness.Service().RestartAsync(Request(batchId: string.Empty), CancellationToken.None);

        report.Outcome.Should().Be(RestartOutcome.RefusedByGuard);
        harness.Launched.Should().BeNull();
    }

    [Fact]
    public async Task AFailedRelaunchDoesNotRetryForever()
    {
        var harness = new Harness { LaunchSucceeds = false };
        var service = harness.Service();

        (await service.RestartAsync(Request(), CancellationToken.None)).Outcome
            .Should().Be(RestartOutcome.RelaunchFailed);

        harness.Exits.Should().BeEmpty("the old process stays up rather than leaving nothing running");

        (await service.RestartAsync(Request(), CancellationToken.None)).Outcome
            .Should().Be(RestartOutcome.RefusedByGuard, "the batch spent its one attempt on the attempt, not on the success");
    }

    [Fact]
    public async Task TheSettingTurnsTheWholeThingOff()
    {
        var harness = new Harness { Enabled = false };
        var service = harness.Service();

        service.Inspect(Request()).Outcome.Should().Be(RestartOutcome.RefusedBySetting);

        var report = await service.RestartAsync(Request(), CancellationToken.None);

        report.Outcome.Should().Be(RestartOutcome.RefusedBySetting);
        harness.Launched.Should().BeNull();
        harness.Guard.IsSpent("batch-1").Should().BeFalse();
    }

    [Fact]
    public void TheNoticeCountsDownAndCanBePutOff()
    {
        var dismissed = 0;
        var restarts = 0;

        var notice = new RestartNoticeViewModel(
            Request(),
            "EuroLLM is installed.",
            [],
            _ =>
            {
                restarts++;
                return Task.FromResult(new RestartReport { Outcome = RestartOutcome.Relaunched, Detail = "done" });
            },
            () => dismissed++,
            _ => { },
            countdownSeconds: 3);

        notice.IsCountingDown.Should().BeTrue();
        notice.CountdownLabel.Should().Be("3 seconds");
        notice.Body.Should().Contain("EuroLLM is installed.");

        notice.Tick();
        notice.CountdownLabel.Should().Be("2 seconds");
        restarts.Should().Be(0, "the countdown is a chance to stop it, not a formality");

        notice.PostponeCommand.Execute(null);
        dismissed.Should().Be(1);

        notice.Tick();
        notice.Tick();
        notice.Tick();
        restarts.Should().Be(0, "a postponed notice does not fire after it has been put away");
    }

    [Fact]
    public void TheNoticeReachesZeroAndRestarts()
    {
        var restarts = 0;

        var notice = new RestartNoticeViewModel(
            Request(),
            "TranslateGemma is installed.",
            [],
            _ =>
            {
                restarts++;
                return Task.FromResult(new RestartReport { Outcome = RestartOutcome.Relaunched, Detail = "done" });
            },
            () => { },
            _ => { },
            countdownSeconds: 2);

        notice.Tick();
        notice.Tick();

        restarts.Should().Be(1);
        notice.State.Should().Be(RestartNoticeState.Restarting);
    }

    [Fact]
    public void ANoticeHeldByARunningTranslationDoesNotCountDown()
    {
        var restarts = 0;

        var notice = new RestartNoticeViewModel(
            Request(),
            "EuroLLM is installed.",
            ["the translation in this window"],
            _ =>
            {
                restarts++;
                return Task.FromResult(new RestartReport { Outcome = RestartOutcome.Relaunched, Detail = "done" });
            },
            () => { },
            _ => { },
            countdownSeconds: 1);

        notice.IsHeld.Should().BeTrue();
        notice.Title.Should().Contain("when this translation finishes");

        notice.Tick();
        notice.Tick();

        restarts.Should().Be(0, "a countdown that expired into a running translation would be the silent kill");

        notice.CancelWorkAndRestartCommand.Execute(null);
        restarts.Should().Be(1, "the explicit choice is the only way through");
    }

    [Fact]
    public void TurningItOffFromTheNoticeIsRemembered()
    {
        bool? remembered = null;
        var dismissed = 0;

        var notice = new RestartNoticeViewModel(
            Request(),
            "EuroLLM is installed.",
            [],
            _ => Task.FromResult(new RestartReport { Outcome = RestartOutcome.Relaunched, Detail = "done" }),
            () => dismissed++,
            on => remembered = on);

        notice.TurnOffCommand.Execute(null);

        remembered.Should().BeFalse();
        dismissed.Should().Be(1);
    }

    [Fact]
    public void AnInstallBatchNamesWhatWasWritten()
    {
        new InstallBatch("a1", ["EuroLLM"]).Summary.Should().Be("EuroLLM is installed.");
        new InstallBatch("a2", ["EuroLLM", "TranslateGemma"]).Summary
            .Should().Be("EuroLLM and TranslateGemma are installed.");
        new InstallBatch("a3", ["A", "B", "C"]).Summary.Should().Be("A, B and C are installed.");
    }
}
