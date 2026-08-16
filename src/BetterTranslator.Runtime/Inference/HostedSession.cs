using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// The model in a child process, reached over a named pipe.
///
/// One request at a time, which is what the caller already guarantees: the
/// translator holds a semaphore across every generation because the runtime
/// refuses a second while one is live.
///
/// When the child dies -- and the reason this exists is that it can die by
/// <c>abort()</c>, which no handler can intercept -- the pipe breaks, the call
/// fails with a reason a reader can be shown, and the session marks itself dead
/// so the next load starts a fresh one. The window stays up.
/// </summary>
public sealed class HostedSession : IInferenceSession
{
    /// <summary>
    /// How long a stop is given to be answered before the child is written off.
    /// Generous on purpose: it is not the time a generation takes to end, only
    /// the time the child takes to say that it did.
    /// </summary>
    internal static TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(10);

    private readonly Process? _child;
    private readonly Stream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    /// <summary>
    /// Held for the length of one line, never across a generation, so a stop can
    /// take the writer while the request it stops is still being answered.
    /// </summary>
    private readonly SemaphoreSlim _write = new(1, 1);

    private bool _dead;

    /// <summary>Stamps each generation, so a stop can name the one it is for.</summary>
    private int _requests;

    private HostedSession(Process? child, Stream pipe, StreamReader reader, StreamWriter writer)
    {
        _child = child;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    public bool IsAlive => !_dead && _child?.HasExited != true;

    /// <summary>
    /// A session over a pipe somebody else is holding the other end of, with no
    /// child process behind it. For tests of the protocol itself: what a stop
    /// costs is a property of these two ends talking, and it is not observable
    /// through a real child without a model on disk to load into it.
    /// </summary>
    internal static HostedSession Over(Stream pipe)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };

        return new HostedSession(null, pipe, reader, writer);
    }

    /// <summary>Where the host executable sits, beside the application's own binary.</summary>
    public static string HostPath =>
        Path.Combine(AppContext.BaseDirectory, "BetterTranslator.Host.exe");

    public static bool HostExists => File.Exists(HostPath);

    /// <summary>
    /// Starts a child and loads the model in it. Throws
    /// <see cref="BetterRuntimeException"/> with the child's own words when the
    /// model will not load, so the caller reports it exactly as it reports an
    /// in-process failure.
    /// </summary>
    public static async Task<HostedSession> StartAsync(
        string modelPath,
        ModelParams options,
        string? flavor,
        IReadOnlyList<string> searchPaths,
        CancellationToken cancellationToken)
    {
        var name = "bettertranslator-" + Guid.NewGuid().ToString("N");

        var pipe = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Process? child = null;

        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = HostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    name,
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            }) ?? throw new BetterRuntimeException("the inference host would not start");

            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connecting.CancelAfter(TimeSpan.FromSeconds(20));
                await pipe.WaitForConnectionAsync(connecting.Token).ConfigureAwait(false);
            }

            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };

            var hello = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (hello != HostProtocol.ReadySignal)
            {
                throw new BetterRuntimeException("the inference host did not answer");
            }

            var session = new HostedSession(child, pipe, reader, writer);

            var loaded = await session.SendAsync(
                new HostRequest
                {
                    Op = "load",
                    ModelPath = modelPath,
                    ContextTokens = options.NCtx,
                    GpuLayers = options.NGpuLayers,
                    Flavor = flavor,
                    SearchPaths = searchPaths,
                },
                cancellationToken).ConfigureAwait(false);

            if (!loaded.Ok)
            {
                session.Dispose();
                throw new BetterRuntimeException(loaded.Error ?? "the model would not load");
            }

            // What the child resolved, carried back into the parent's own state
            // so the status panel reports the library that is running rather
            // than the one that was requested.
            LocalTranslator.RecordLoaded(loaded.Flavor ?? flavor, loaded.FlavorNote);

            return session;
        }
        catch
        {
            Kill(child);
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reports a dead child rather than throwing about it. This is the call that
    /// runs inside a <see cref="Task.Run"/>, and an exception raised there has no
    /// user frame above it to be caught by, so every crash stopped a debugger and
    /// was reported as unhandled even though the awaiting code handled it.
    /// </summary>
    public Completion CompleteCounted(string prompt, GenParams sampling, CancellationToken cancellationToken)
    {
        var request = new HostRequest
        {
            Op = HostProtocol.Complete,
            Id = Interlocked.Increment(ref _requests),
            Text = prompt,
            MaxTokens = sampling.MaxTokens,
            Temperature = sampling.Temperature,
            TopP = sampling.TopP,
            TopK = sampling.TopK,
            RepeatPenalty = sampling.RepeatPenalty,
            Seed = sampling.Seed,
        };

        var answer = SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        return answer.Ok
            ? new Completion(answer.Text ?? string.Empty, answer.Tokens, null)
            : Completion.Failed(answer.Error ?? "the inference host failed");
    }

    public string BuildTranslatePrompt(string text, Language from, Language to)
    {
        var answer = Send(
            new HostRequest
            {
                Op = "prompt",
                Text = text,
                FromCode = from.Code,
                FromName = from.Name,
                ToCode = to.Code,
                ToName = to.Name,
            },
            CancellationToken.None);

        return answer.Text ?? string.Empty;
    }

    private HostResponse Send(HostRequest request, CancellationToken cancellationToken)
    {
        var answer = SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        return answer.Ok
            ? answer
            : throw new BetterRuntimeException(answer.Error ?? "the inference host failed");
    }

    /// <summary>
    /// A dead child comes back as a failed response, never as an exception.
    ///
    /// This class exists because the child can die, so its death is the expected
    /// case and not an exceptional one -- and raising it here would raise it
    /// inside the <see cref="Task.Run"/> the caller wraps this in, where no frame
    /// above it can catch it and a debugger stops on every occurrence. Callers
    /// that want an exception raise their own, on their own stack.
    ///
    /// Cancellation still propagates: that one is genuinely exceptional and every
    /// caller already expects it.
    /// </summary>
    private async Task<HostResponse> SendAsync(HostRequest request, CancellationToken cancellationToken)
    {
        if (_dead)
        {
            return HostResponse.Failed("the inference host is no longer running");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Stopped before anything went out. Nothing is half-written and the
            // child was never asked, so the pipe is still in step and this costs
            // the session nothing.
            throw new OperationCanceledException(cancellationToken);
        }

        try
        {
            await WriteAsync(HostProtocol.Encode(request), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Half a request on the wire is not something the next one can
            // follow. This is the one cancellation that really does end the
            // child.
            _dead = true;
            throw;
        }
        catch (IOException)
        {
            _dead = true;
            return HostResponse.Failed(Died());
        }
        catch (ObjectDisposedException)
        {
            _dead = true;
            return HostResponse.Failed("the inference host was shut down");
        }

        try
        {
            var line = await AnswerAsync(request, cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                // The child stopped answering. Given what this class exists for,
                // the overwhelmingly likely reason is that it aborted, and its
                // exit code says which.
                _dead = true;
                return HostResponse.Failed(Died());
            }

            var response = HostProtocol.Decode<HostResponse>(line)
                ?? HostResponse.Failed("the inference host sent something unreadable");

            if (response.Cancelled)
            {
                // The child answered the request it was told to stop, so the
                // pipe is in step and it is still holding the model. The caller
                // gets the cancellation it expects; the session does not die of
                // it, which is the whole point of asking rather than killing.
                throw new OperationCanceledException(cancellationToken);
            }

            return response;
        }
        catch (IOException)
        {
            _dead = true;
            return HostResponse.Failed(Died());
        }
        catch (ObjectDisposedException)
        {
            _dead = true;
            return HostResponse.Failed("the inference host was shut down");
        }
    }

    /// <summary>
    /// Waits for the one line this request is owed.
    ///
    /// Cancelling does not abandon the read. A half-read pipe cannot be reused,
    /// so abandoning it is what used to cost the child process and the loaded
    /// model on every stop -- the next send paid a full reload for a button that
    /// promises the opposite. The child is asked to stop instead, and its answer
    /// is still read: it comes back marked cancelled, in step, and the session
    /// lives.
    ///
    /// Only a generation can be stopped this way. Everything else keeps the old
    /// behaviour, because nothing in the child is watching a token during a load
    /// and a stop it cannot perform would never be answered.
    /// </summary>
    private async Task<string?> AnswerAsync(HostRequest request, CancellationToken cancellationToken)
    {
        var read = _reader.ReadLineAsync(CancellationToken.None).AsTask();

        if (!cancellationToken.CanBeCanceled)
        {
            return await read.ConfigureAwait(false);
        }

        if (request.Op != HostProtocol.Complete)
        {
            try
            {
                return await read.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _dead = true;
                throw;
            }
        }

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = Task.FromResult(false);

        // The callback runs on whoever called Cancel, which for the Pause button
        // is the UI thread. It starts the write and returns; it does not wait
        // for the pipe.
        var registration = cancellationToken.Register(() =>
        {
            sending = SendCancelAsync(request.Id);
            stopped.TrySetResult();
        });

        try
        {
            if (await Task.WhenAny(read, stopped.Task).ConfigureAwait(false) == read)
            {
                return await read.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister();
        }

        if (!await sending.ConfigureAwait(false))
        {
            // The stop never left. Waiting out the grace period would be waiting
            // for an answer to a request the child was never given.
            _dead = true;
            throw new OperationCanceledException(cancellationToken);
        }

        // A child that will not answer a stop it received is not in step with
        // anything, and waiting on it forever would hang the window rather than
        // the generation. This is the one case where killing it is still right.
        using var graced = new CancellationTokenSource();

        if (await Task.WhenAny(read, Task.Delay(StopGrace, graced.Token)).ConfigureAwait(false) != read)
        {
            _dead = true;
            throw new OperationCanceledException(cancellationToken);
        }

        await graced.CancelAsync().ConfigureAwait(false);

        return await read.ConfigureAwait(false);
    }

    private async Task WriteAsync(string line, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// Overtakes a running generation on the same pipe, naming the generation it
    /// stops. The writer is only ever held for the length of one line, so it is
    /// free while the child generates; the brief wait covers the moment a send is
    /// still writing its own request.
    ///
    /// Answers whether the stop actually left, and never throws: this runs from a
    /// cancellation callback, where an escaping exception comes back out of
    /// <see cref="CancellationTokenSource.Cancel()"/> on whatever thread pressed
    /// the button. A stop that did not leave is reported rather than swallowed,
    /// because waiting out a grace period for it would be waiting on nothing.
    /// </summary>
    private async Task<bool> SendCancelAsync(int id)
    {
        try
        {
            if (!await _write.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }

        try
        {
            var line = HostProtocol.Encode(new HostRequest { Op = HostProtocol.Cancel, Id = id });

            await _writer.WriteLineAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try
            {
                _write.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// What to tell the reader. The fast-fail code is worth naming: it is the
    /// difference between "the model refused this" and "the runtime crashed on
    /// this", and only one of those is worth retrying.
    /// </summary>
    private string Died()
    {
        var code = ExitCode();

        return code switch
        {
            null => "the model runtime stopped unexpectedly",
            unchecked((int)0xC0000409) => "the model runtime crashed while generating (fatal runtime fault)",
            0 => "the model runtime exited",
            _ => $"the model runtime stopped unexpectedly (exit code 0x{code:X8})",
        };
    }

    private int? ExitCode()
    {
        try
        {
            // A moment for the exit to be recorded: the pipe usually breaks
            // fractionally before the process is reaped.
            return _child is not null && _child.WaitForExit(TimeSpan.FromSeconds(2)) ? _child.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _dead = true;

        try
        {
            _writer.Dispose();
            _reader.Dispose();
            _pipe.Dispose();
        }
        catch (IOException)
        {
        }

        Kill(_child);
        _child?.Dispose();
        _write.Dispose();
    }

    private static void Kill(Process? child)
    {
        if (child is null)
        {
            return;
        }

        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
