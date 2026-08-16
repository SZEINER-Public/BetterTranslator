using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using BetterTranslator.Runtime;
using BetterTranslator.Runtime.Inference;

// The process that holds the model, so the process that holds the window does
// not. Everything above the native call -- prompts, gates, terminology -- stays
// in the app; only the part that can abort() lives here.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: BetterTranslator.Host <pipe-name> <parent-pid>");
    return 2;
}

var pipeName = args[0];

if (!int.TryParse(args[1], out var parentId))
{
    Console.Error.WriteLine("the parent process id is not a number");
    return 2;
}

WatchParent(parentId);

await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

try
{
    await pipe.ConnectAsync(20_000);
}
catch (TimeoutException)
{
    Console.Error.WriteLine("the parent never accepted the connection");
    return 3;
}

using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };

await writer.WriteLineAsync(HostProtocol.ReadySignal);

BetterRuntimeModel? model = null;

// The loop is not allowed to sit inside a generation. A stop is only worth
// sending while the thing it stops is still running, so it has to be readable
// then -- which means the work runs off the loop and the loop keeps reading.
//
// Ordering is kept by chaining rather than by blocking: each request waits for
// the one before it, so the parent still gets exactly one line back per line it
// sent, in the order it sent them.
var pending = Task.CompletedTask;

// What is generating, and what a stop has asked for. Both are read by the
// loop thread and written by the worker, so they move together under one lock
// rather than separately and out of step.
var gate = new object();
CancellationTokenSource? live = null;
var liveId = 0;
var stopWanted = 0;

try
{
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length == 0)
        {
            continue;
        }

        HostRequest? request;

        try
        {
            request = HostProtocol.Decode<HostRequest>(line);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            request = null;
        }

        if (request is null)
        {
            pending = Chain(pending, () => Task.FromResult(HostResponse.Failed("unreadable request")));
            continue;
        }

        if (request.Op == HostProtocol.Cancel)
        {
            // Answered by the generation it stops, never by itself. Recorded
            // as well as delivered: this loop runs ahead of the work, so the
            // generation being stopped may not have taken its token yet, and a
            // stop that arrived first would otherwise be dropped -- leaving the
            // parent waiting out a grace period for an answer nobody was going
            // to give. A stop for something already finished is not an error.
            CancellationTokenSource? stop = null;

            lock (gate)
            {
                stopWanted = request.Id;

                if (liveId != 0 && liveId == request.Id)
                {
                    stop = live;
                }
            }

            try
            {
                stop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            continue;
        }

        var arrived = request;
        pending = Chain(pending, () => ServeAsync(arrived));
    }

}
catch (IOException)
{
    // The parent went away mid-conversation. Nothing to report to.
}
finally
{
    // Before the model goes. A generation dispatched off the loop may still be
    // inside a native call on it, and freeing the model under one is not a
    // crash this process can report -- it is the abort() this process exists to
    // contain.
    try
    {
        await pending;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
    }

    model?.Dispose();
}

return 0;

Task Chain(Task previous, Func<Task<HostResponse>> work) =>
    previous.ContinueWith(
        async _ =>
        {
            var response = await work().ConfigureAwait(false);

            try
            {
                await writer.WriteLineAsync(HostProtocol.Encode(response)).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The parent went away. Nothing to report to.
            }
        },
        TaskScheduler.Default).Unwrap();

async Task<HostResponse> ServeAsync(HostRequest request)
{
    // Only a generation can be stopped, and only a generation is worth moving
    // off this thread. Loading is left where it was: it holds no token the
    // runtime honours, and pretending otherwise would answer a stop it cannot
    // perform.
    if (request.Op != HostProtocol.Complete)
    {
        try
        {
            var (answer, loaded) = Handle(request, model, CancellationToken.None);
            model = loaded;
            return answer;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HostResponse.Failed(ex.Message);
        }
    }

    using var stopping = new CancellationTokenSource();
    bool alreadyStopped;

    lock (gate)
    {
        live = stopping;
        liveId = request.Id;
        alreadyStopped = stopWanted != 0 && stopWanted == request.Id;
    }

    if (alreadyStopped)
    {
        stopping.Cancel();
    }

    try
    {
        var (answer, loaded) = await Task
            .Run(() => Handle(request, model, stopping.Token))
            .ConfigureAwait(false);

        model = loaded;
        return answer;
    }
    catch (OperationCanceledException)
    {
        // The model is untouched by this: br_gen_cancel ends the generation,
        // not the context. Saying so is what lets the parent keep the session.
        return HostResponse.Stopped();
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        return HostResponse.Failed(ex.Message);
    }
    finally
    {
        lock (gate)
        {
            live = null;
            liveId = 0;
        }
    }
}

static (HostResponse Response, BetterRuntimeModel? Model) Handle(
    HostRequest request,
    BetterRuntimeModel? model,
    CancellationToken cancellationToken)
{
    switch (request.Op)
    {
        case "load":
            {
                model?.Dispose();
                model = Load(request);

                // The resolution happened here, in this process, so this is the
                // only place that knows what actually loaded. Answered on every
                // load rather than on request: the parent cannot know to ask.
                var (loaded, note) = LocalTranslator.ResolvedFlavor;

                return (new HostResponse { Ok = true, Flavor = loaded, FlavorNote = note }, model);
            }

        case "prompt":
            {
                if (model is null)
                {
                    return (HostResponse.Failed("no model is loaded"), model);
                }

                var text = model.BuildTranslatePrompt(
                    request.Text ?? string.Empty,
                    new Language(request.FromCode ?? string.Empty, request.FromName ?? string.Empty),
                    new Language(request.ToCode ?? string.Empty, request.ToName ?? string.Empty));

                return (new HostResponse { Ok = true, Text = text }, model);
            }

        case "complete":
            {
                if (model is null)
                {
                    return (HostResponse.Failed("no model is loaded"), model);
                }

                var sampling = new GenParams
                {
                    MaxTokens = request.MaxTokens,
                    Temperature = request.Temperature,
                    TopP = request.TopP,
                    TopK = request.TopK,
                    RepeatPenalty = request.RepeatPenalty,
                    Seed = request.Seed,
                };

                var (answer, tokens) = model.CompleteCounted(
                    request.Text ?? string.Empty,
                    sampling,
                    cancellationToken);

                return (new HostResponse { Ok = true, Text = answer, Tokens = tokens }, model);
            }

        default:
            return (HostResponse.Failed($"unknown op '{request.Op}'"), model);
    }
}

static BetterRuntimeModel Load(HostRequest request)
{
    foreach (var folder in request.SearchPaths ?? [])
    {
        BackendCatalog.SearchAlso(folder);
    }

    if (!string.IsNullOrWhiteSpace(request.Flavor))
    {
        LocalTranslator.PreferFlavor(request.Flavor);
    }

    return new BetterRuntimeModel(
        request.ModelPath ?? string.Empty,
        new ModelParams { NCtx = request.ContextTokens, NGpuLayers = request.GpuLayers });
}

// A child holding gigabytes of model is not something to leave behind. The pipe
// breaking covers an orderly exit; this covers the parent being killed.
static void WatchParent(int parentId)
{
    try
    {
        var parent = Process.GetProcessById(parentId);
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => Environment.Exit(0);

        if (parent.HasExited)
        {
            Environment.Exit(0);
        }
    }
    catch (ArgumentException)
    {
        Environment.Exit(0);
    }
}
