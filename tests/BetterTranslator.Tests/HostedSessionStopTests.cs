using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What stopping a generation costs.
///
/// The child used to be killed for it -- the read was abandoned mid-answer, the
/// pipe could not be resynchronised, and the next send paid a full model load --
/// while the button that does it promises the model stays put. These drive both
/// ends of the real protocol over a real pipe, with a stand-in for the child,
/// because that promise is a property of the two ends talking and nothing in a
/// child process is observable without a model on disk to load into it.
/// </summary>
public sealed class HostedSessionStopTests
{
    [Fact]
    public async Task AStoppedGenerationLeavesTheSessionAliveAndTheNextSendWorks()
    {
        await using var host = await FakeHost.StartAsync();

        using var stopping = new CancellationTokenSource();

        var generating = Task.Run(() =>
            host.Session.CompleteCounted("translate this", new GenParams { MaxTokens = 64 }, stopping.Token));

        await host.Generating;
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generating);

        host.WasAskedToStop.Should().BeTrue("the child is asked to stop, not abandoned");
        host.Session.IsAlive.Should().BeTrue("a stop the child answered is not a death");

        var next = host.Session.BuildTranslatePrompt("after", new Language("en", "English"), new Language("cs", "Czech"));

        next.Should().Be("prompt:after", "the pipe is still in step, so the session serves the next request");
    }

    /// <summary>
    /// Stopped before the request was ever sent. Nothing is on the wire, so
    /// there is nothing to stop and nothing to resynchronise -- the cheapest
    /// possible cancellation, and it must not cost the session either.
    /// </summary>
    [Fact]
    public async Task AStopBeforeAnythingIsSentCostsNeitherTheChildNorAStopMessage()
    {
        await using var host = await FakeHost.StartAsync();

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var generating = Task.Run(() =>
            host.Session.CompleteCounted("translate this", new GenParams { MaxTokens = 64 }, stopping.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generating);

        host.Generating.IsCompleted.Should().BeFalse("the child was never asked to generate");
        host.WasAskedToStop.Should().BeFalse("nor to stop something that was never started");
        host.Session.IsAlive.Should().BeTrue("and nothing about the pipe changed");
    }

    [Fact]
    public async Task AChildThatWillNotAnswerAStopIsWrittenOffRatherThanWaitedOnForever()
    {
        var grace = HostedSession.StopGrace;
        HostedSession.StopGrace = TimeSpan.FromMilliseconds(250);

        try
        {
            await using var host = await FakeHost.StartAsync(answerStops: false);

            using var stopping = new CancellationTokenSource();

            var generating = Task.Run(() =>
                host.Session.CompleteCounted("translate this", new GenParams { MaxTokens = 64 }, stopping.Token));

            await host.Generating;
            await stopping.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generating);

            host.Session.IsAlive.Should().BeFalse(
                "a child that will not answer a stop is not in step, and waiting on it would hang the window");
        }
        finally
        {
            HostedSession.StopGrace = grace;
        }
    }

    /// <summary>
    /// The real child, not the stand-in. A stop is the one request answered by
    /// something other than itself, so if it ever wrote a line of its own every
    /// read after it would be one line behind -- answers arriving for the wrong
    /// requests, forever. No model is needed to prove it: an unknown op is
    /// answered by name, which is enough to tell the two responses apart.
    /// </summary>
    [Fact]
    public async Task TheRealChildAnswersOneLinePerRequestAndSaysNothingToAStop()
    {
        var name = "bt-host-" + Guid.NewGuid().ToString("N");

        using var pipe = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = HostedSession.HostPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                name,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            },
        })!;

        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await pipe.WaitForConnectionAsync(patience.Token);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            var ready = await reader.ReadLineAsync(patience.Token);
            ready.Should().Be(HostProtocol.ReadySignal);

            await writer.WriteLineAsync(HostProtocol.Encode(new HostRequest { Op = "alpha" }));

            var first = HostProtocol.Decode<HostResponse>((await reader.ReadLineAsync(patience.Token))!);
            first!.Error.Should().Contain("alpha");

            await writer.WriteLineAsync(HostProtocol.Encode(new HostRequest { Op = HostProtocol.Cancel }));
            await writer.WriteLineAsync(HostProtocol.Encode(new HostRequest { Op = "beta" }));

            var second = HostProtocol.Decode<HostResponse>((await reader.ReadLineAsync(patience.Token))!);

            second!.Error.Should().Contain(
                "beta",
                "the next line back belongs to the next request, so the stop wrote nothing");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void ACancelledAnswerIsMarkedAsSuchAndSurvivesTheWire()
    {
        var decoded = HostProtocol.Decode<HostResponse>(HostProtocol.Encode(HostResponse.Stopped()));

        decoded.Should().NotBeNull();
        decoded!.Cancelled.Should().BeTrue();
        decoded.Ok.Should().BeFalse("a stopped generation produced no translation");

        HostProtocol.Decode<HostResponse>(HostProtocol.Encode(HostResponse.Failed("boom")))!
            .Cancelled.Should().BeFalse("only a stop is a stop");
    }

    /// <summary>
    /// The other end of the pipe: answers a prompt at once, and holds a
    /// completion open until it is told to stop, which is the only window in
    /// which a stop means anything.
    /// </summary>
    private sealed class FakeHost : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _parent;
        private readonly NamedPipeClientStream _child;
        private readonly TaskCompletionSource _generating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serving;

        private Task _answering = Task.CompletedTask;

        private FakeHost(NamedPipeServerStream parent, NamedPipeClientStream child, bool answerStops)
        {
            _parent = parent;
            _child = child;
            Session = HostedSession.Over(parent);
            _serving = Task.Run(() => ServeAsync(answerStops));
        }

        public HostedSession Session { get; }

        public Task Generating => _generating.Task;

        public bool WasAskedToStop { get; private set; }

        public static async Task<FakeHost> StartAsync(bool answerStops = true)
        {
            var name = "bt-test-" + Guid.NewGuid().ToString("N");

            var parent = new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var child = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

            var waiting = parent.WaitForConnectionAsync();
            await child.ConnectAsync(5_000).ConfigureAwait(false);
            await waiting.ConfigureAwait(false);

            return new FakeHost(parent, child, answerStops);
        }

        public async ValueTask DisposeAsync()
        {
            Session.Dispose();
            await _child.DisposeAsync().ConfigureAwait(false);
            await _parent.DisposeAsync().ConfigureAwait(false);

            _generating.TrySetResult();
            _stop.TrySetResult();

            try
            {
                await Task.WhenAll(_serving, _answering).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async Task ServeAsync(bool answerStops)
        {
            using var reader = new StreamReader(_child, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(_child, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var request = HostProtocol.Decode<HostRequest>(line);

                if (request is null)
                {
                    continue;
                }

                if (request.Op == HostProtocol.Cancel)
                {
                    WasAskedToStop = true;
                    _stop.TrySetResult();
                    continue;
                }

                if (request.Op == HostProtocol.Complete)
                {
                    _generating.TrySetResult();

                    if (!answerStops)
                    {
                        continue;
                    }

                    // Answered when the stop arrives, and not by waiting here
                    // for it: the stop comes down this same pipe, so a loop
                    // that blocks on it can never read it. That is the whole
                    // shape of the fix being tested.
                    _answering = _stop.Task.ContinueWith(
                        _ => writer.WriteLineAsync(HostProtocol.Encode(HostResponse.Stopped())),
                        TaskScheduler.Default).Unwrap();

                    continue;
                }

                await writer
                    .WriteLineAsync(HostProtocol.Encode(new HostResponse { Ok = true, Text = "prompt:" + request.Text }))
                    .ConfigureAwait(false);
            }
        }
    }
}
