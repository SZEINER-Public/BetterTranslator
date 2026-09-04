using BetterTranslator.Core.Models;

namespace BetterTranslator.Runtime.Inference;

public enum TranslatorState
{
    /// <summary>Nothing loaded. No model has been asked for yet.</summary>
    Idle,

    /// <summary>Reading a model off disk. Seconds, not milliseconds.</summary>
    Starting,

    /// <summary>Loaded and able to translate.</summary>
    Ready,

    /// <summary>The last start failed. Reason carries the detail.</summary>
    Failed,
}

/// <summary>
/// Owns the in-process runtime and the one model loaded into it.
///
/// Started on the first translation rather than at launch, because loading a
/// model costs seconds and gigabytes and most sessions open the window before
/// they translate anything. Reloading is by model path: swapping models is
/// cheap enough to do on demand, whereas swapping backends is not possible at
/// all once a native library is loaded, so that one waits for a restart.
/// </summary>
public sealed class LocalTranslator : IDisposable
{
    /// <summary>
    /// Never the model's own maximum. A 131072 context allocates roughly
    /// 14.7 GB for a 2.3 GB model, which is the runtime's default of 4096 being
    /// there for a reason.
    /// </summary>
    private const int ContextTokens = 4096;

    private static RuntimeBackend _backend = RuntimeBackend.Cpu;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IInferenceSession? _model;
    private string? _loadedPath;

    /// <summary>
    /// Cancels whatever generation is running. Held here rather than passed in,
    /// because the thing that stops a run is usually not the thing that started
    /// it: the send path owns the call, and the button that stops it is in the
    /// window chrome.
    /// </summary>
    private CancellationTokenSource? _running;

    /// <summary>True while a generation is actually in flight.</summary>
    public bool IsGenerating => _running is not null;

    /// <summary>
    /// Where the last prompt came from -- "Gemma / TranslateGemma", "ChatML /
    /// engine", or "runtime" when the model's own template could not be
    /// rendered. Reported rather than inferred: which prompt a model got is the
    /// difference between using its fine-tune and throwing it away, and it is
    /// invisible in the output.
    /// </summary>
    public string? LastPromptSource { get; private set; }

    /// <summary>
    /// Why the last answer was refused or repaired, or null when it came through
    /// clean. Surfaced rather than swallowed: a chunk that silently kept its
    /// source looks identical to one nothing was ever sent for.
    /// </summary>
    public string? LastGuardVerdict { get; private set; }

    /// <summary>
    /// The do-not-translate lists in force, loaded once. Configuration rather
    /// than constants: which list a term belongs on is a decision only the
    /// operator can make, and seven copies of a literal array in the reference
    /// had already drifted apart before it was moved to a file.
    /// </summary>
    private static readonly Engine.Slop.DoNotTranslateLists Terms = Engine.Slop.DoNotTranslate.Load();

    public TranslatorState State { get; private set; } = TranslatorState.Idle;

    public string? Reason { get; private set; }

    /// <summary>The model currently in memory, for a status line.</summary>
    public string? LoadedModelName => _loadedPath is null ? null : Path.GetFileNameWithoutExtension(_loadedPath);

    /// <summary>Raised on every state change, so a view model can follow along.</summary>
    public event Action? Changed;

    /// <summary>
    /// The flavour to load. Read once, at the first native call: Windows will
    /// not swap a loaded DLL, so setting this after the runtime has started
    /// changes nothing until the next launch.
    /// </summary>
    /// <summary>The backend in force, so a test can put back what it found.</summary>
    internal static RuntimeBackend Backend => _backend;

    public static void Prefer(RuntimeBackend backend)
    {
        _backend = backend;
        Flavor = Path.GetFileNameWithoutExtension(BackendCatalog.FileNameFor(backend));
        Native.PreferredFlavor = Flavor;
    }

    /// <summary>
    /// The same choice, for a process that has no settings of its own. The host
    /// is told which flavour to load rather than working it out, so parent and
    /// child cannot resolve differently.
    /// </summary>
    public static void PreferFlavor(string flavor)
    {
        Flavor = flavor;
        Native.PreferredFlavor = flavor;
    }

    /// <summary>The resolved flavour name, or null while nothing has chosen one.</summary>
    public static string? Flavor { get; private set; }

    /// <summary>
    /// The flavour that actually loaded. Not the same question as
    /// <see cref="Flavor"/>, which is what was asked for, and the two came apart
    /// in the field: a CUDA build with no cuBLAS beside it left the settings
    /// screen saying CUDA while every token came off the processor.
    ///
    /// Set from the child process where there is one, because that is where the
    /// library is actually loaded and the parent has no way to know otherwise.
    /// </summary>
    public static string? LoadedFlavor { get; private set; }

    /// <summary>Why the two differ, in one sentence, or null when they do not.</summary>
    public static string? FlavorNote { get; private set; }

    /// <summary>
    /// Records what the load really resolved to. Called with the child's answer
    /// when hosted, and with this process's own when not.
    /// </summary>
    internal static void RecordLoaded(string? loaded, string? note)
    {
        LoadedFlavor = loaded;
        FlavorNote = note;
    }

    /// <summary>What this process resolved, for a host reporting back to its parent.</summary>
    public static (string? Loaded, string? Note) ResolvedFlavor => (Native.LoadedFlavor, Native.Substitution);

    /// <summary>
    /// Run the model in a child process. On by default, and the reason is a
    /// measured crash rather than tidiness: the native runtime can abort, and an
    /// abort in this process ends the window with no dialog and no log. It falls
    /// back to in-process on its own when the host cannot be started, so a build
    /// or install without the host still translates.
    /// </summary>
    public static bool UseHost { get; set; } = true;

    /// <summary>
    /// How many layers to hand the GPU. br_model_params_default returns 0 --
    /// measured -- so a zeroed struct runs a GPU flavour entirely on the CPU:
    /// the Vulkan library loads, reports itself, and is never asked to do any
    /// work. -1 offloads all of them.
    /// </summary>
    private static int GpuLayers => _backend == RuntimeBackend.Cpu ? 0 : -1;

    /// <summary>
    /// Loads the model if it is not already the one in memory. Safe to call
    /// before every translation; it returns immediately once warm.
    /// </summary>
    public async Task<bool> EnsureLoadedAsync(string modelPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return Fail("No model selected");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A session that has died is not the model being asked for, however
            // its path compares. This is the line that turns a crashed host into
            // a reload rather than a permanently broken translator.
            if (_model is { IsAlive: true }
                && string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(modelPath))
            {
                return Fail($"Model file is missing: {modelPath}");
            }

            Set(TranslatorState.Starting, null);

            _model?.Dispose();
            _model = null;
            _loadedPath = null;

            try
            {
                _model = await OpenAsync(modelPath, cancellationToken).ConfigureAwait(false);

                _loadedPath = modelPath;
                Set(TranslatorState.Ready, null);
                return true;
            }
            catch (BetterRuntimeException ex)
            {
                return Fail(ex.Message);
            }
            catch (DllNotFoundException ex)
            {
                return Fail($"The runtime library is missing. {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces a session that died under a send in progress.
    ///
    /// The caller is inside the generation gate and must stay there, so this
    /// deliberately does not take it -- going through
    /// <see cref="EnsureLoadedAsync"/> from here would wait on a semaphore this
    /// thread is already holding. Null when the replacement will not start, and
    /// then the send fails with the reason rather than looping.
    /// </summary>
    private async Task<IInferenceSession?> RestartAsync(CancellationToken cancellationToken)
    {
        var path = _loadedPath;

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        _model?.Dispose();
        _model = null;

        try
        {
            _model = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
            _loadedPath = path;
            Set(TranslatorState.Ready, null);

            return _model;
        }
        catch (Exception ex) when (ex is BetterRuntimeException or DllNotFoundException)
        {
            _loadedPath = null;
            Fail(ex.Message);

            return null;
        }
    }

    /// <summary>
    /// Opens the model where it is meant to live: a child process by default,
    /// this one when the host is absent or will not start.
    ///
    /// The fallback is deliberate and quiet. A host that cannot start is a
    /// packaging problem, and answering it by refusing to translate would turn a
    /// crash-resistance measure into a reason nothing works at all.
    /// </summary>
    private async Task<IInferenceSession> OpenAsync(string modelPath, CancellationToken cancellationToken)
    {
        var options = new ModelParams { NCtx = ContextTokens, NGpuLayers = GpuLayers };

        if (UseHost && HostedSession.HostExists)
        {
            try
            {
                var hosted = await HostedSession
                    .StartAsync(modelPath, options, Flavor, BackendCatalog.SearchPaths, cancellationToken)
                    .ConfigureAwait(false);

                HostedInference = true;
                return hosted;
            }
            catch (BetterRuntimeException)
            {
                // The model itself would not load. That is the same answer in
                // either process, so it is reported rather than retried here.
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastHostProblem = ex.Message;
            }
        }

        HostedInference = false;

        // Off the UI thread: this reads gigabytes.
        var model = await Task.Run(() => new BetterRuntimeModel(modelPath, options), cancellationToken)
            .ConfigureAwait(false);

        // The resolver ran in this process on that call, so ask it directly.
        // Same fact as the hosted branch records, from the other source.
        var (loaded, note) = ResolvedFlavor;
        RecordLoaded(loaded, note);

        return new InProcessSession(model);
    }

    /// <summary>True while the loaded model is living in the child process.</summary>
    public bool HostedInference { get; private set; }

    /// <summary>Why the host was not used, when it was meant to be. Null otherwise.</summary>
    public string? LastHostProblem { get; private set; }

    /// <summary>
    /// Translates, loading the model first if needed. Returns null rather than
    /// throwing when the runtime is unavailable, so a failure shows as an
    /// untranslated entry instead of taking the send path down with it.
    ///
    /// Nothing is carried between calls. Each one builds its whole prompt from
    /// the job it was given, so a hundred sends in one chat cost the same
    /// context as the first, and one chat cannot leak into the next. Anything
    /// the model is meant to remember has to arrive in Memory -- which is the
    /// composer's Memory chip, and only that.
    /// </summary>
    public IInferenceSession? Session => _model;

    public TranslationJob? LastJob { get; private set; }

    public async Task<TranslationOutcome> TranslateAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        LastJob = job;

        // The verdict belongs to this send. Not clearing it let the runtime and
        // cancellation paths, which never write one, hand a caller the previous
        // send's reason and let it be reported against this one.
        LastGuardVerdict = null;

        // Refused before the model is even loaded. Asked to translate English
        // into English every model returns the text unchanged -- correctly -- and
        // the pipeline then refuses that as "unchanged", so the user waits half a
        // minute to be shown their own text with no reason given.
        if (job.IsSameLanguage)
        {
            return TranslationOutcome.None(clock.Elapsed) with
            {
                Direction = new Core.Languages.DirectionVerdict(Core.Languages.DirectionStatus.SameLanguage)
                {
                    LanguageName = job.To.Name,
                },
            };
        }

        if (!await EnsureLoadedAsync(job.ModelPath, cancellationToken).ConfigureAwait(false))
        {
            // The runtime's own reason, carried where a gate's reason would go. A
            // model that will not load produced no answer for a reason nothing
            // else on screen can state, and "no translation, no explanation" is
            // the same dead end whether a gate or the loader caused it.
            LastGuardVerdict = Reason is { Length: > 0 } ? Reason : "the model could not be loaded";
            return TranslationOutcome.None(clock.Elapsed);
        }

        // One generation at a time per model is the runtime's own contract:
        // br_gen_start refuses a second while one is live.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Linked, so the caller's token still cancels and the stop button also
        // can. Published before the run starts and cleared in the finally, which
        // is what IsGenerating reads.
        var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;

        // Changed means "something about me is different", and whether a
        // generation is in flight is exactly that: it decides whether the pause
        // control has anything to act on.
        Changed?.Invoke();

        try
        {
            if (_model is null)
            {
                LastGuardVerdict = "the model was unloaded before this send started";
                return TranslationOutcome.None(clock.Elapsed);
            }

            var model = _model;
            cancellationToken = running.Token;

            // Preferred: build the whole prompt from what the model itself
            // declares -- its turn markers out of its GGUF header, and the
            // instruction it was trained on where model-prompts.json lists one.
            // The runtime builds exactly one prompt for every model it loads,
            // which is right for TranslateGemma and wrong twice over for
            // anything else.
            // The configuration for this target language: its rules, its slop
            // tiers, its glossary. Cached per language, because reading the
            // rules file on every chunk would let it change mid-document.
            var rag = Engine.Slop.RagConfig.For(job.To.Code);

            var preamble = GroundedPrompt.Block(job.Memory, job.Instruction);

            // Only the glossary terms actually present in this text, so the
            // prompt does not carry a dictionary the model has to read past --
            // and only when a project is in scope, because a glossary is a
            // statement about one body of documents rather than about the
            // language.
            if (job.UseProjectVocabulary)
            {
                var hint = Engine.Slop.Glossary.Hint(rag.Glossary, job.Text, job.To.Name);

                if (hint.Length > 0)
                {
                    preamble = (preamble ?? string.Empty) + hint.Trim();
                }
            }

            string Prompt(string body)
            {
                var shaped = Engine.Models.TranslationPromptBuilder.Build(
                    job.ModelPath,
                    body,
                    job.From.Name,
                    job.From.Code,
                    job.To.Name,
                    job.To.Code,
                    preamble,
                    rag.RulesText);

                LastPromptSource = shaped is null
                    ? "runtime"
                    : $"{shaped.TemplateName} / {shaped.InstructionSource}";

                // The model declares a template this build cannot render. Fall
                // back to the runtime's own prompt, which is what happened before
                // any of this existed, rather than to a shape assembled from a
                // guess.
                return shaped?.Text
                    ?? GroundedPrompt.Splice(
                        inner => model.BuildTranslatePrompt(inner, job.From, job.To),
                        body,
                        job.Memory,
                        job.Instruction)
                    ?? model.BuildTranslatePrompt(body, job.From, job.To);
            }

            // Placeholders are hidden behind sentinels so the model cannot
            // translate them, and only for text somebody typed: a document chunk
            // carries markup of its own that this would compete with.
            var guards = job.IsStandalone
                ? Engine.Markup.PlaceholderGuard.Protect(job.Text)
                : Engine.Markup.PlaceholderGuards.None;

            if (guards.Any)
            {
                // Ahead of the translate instruction, which is the only hole in
                // the trained branch's prompt. Without it TranslateGemma is handed
                // [[0]] having never been told what it is: the engine's own system
                // prompt explains the form, and the trained branch does not send
                // the engine's system prompt.
                preamble = (preamble ?? string.Empty) + Engine.Markup.PlaceholderGuard.Instruction + "\n\n";
            }

            var prompt = Prompt(job.Text);

            // The pipeline's own discipline: try, run every gate in order, and
            // retry once on a refusal before giving up and keeping the source.
            // A model handed the same prompt twice does not give the same answer
            // twice, so one retry converts a large share of refusals into
            // accepted translations -- which is why the reference retries rather
            // than failing on the first verdict.
            var totalTokens = 0;
            string? reason = null;

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                // The seed moves with the attempt. Without that the retry sends a
                // request identical in every respect and gets back the same
                // bytes, so it is a full generation spent to be refused again for
                // the same reason.
                var sampling = job.Sampling(attempt);

                // Every attempt, not all but the last. Dropping the protection to
                // salvage an attempt is what let a placeholder come back
                // translated, and the structural pass below covers the case this
                // used to cover.
                var protect = guards.Any;
                var sent = protect ? Prompt(guards.Text) : prompt;

                var completion = await Task
                    .Run(() => model.CompleteCounted(sent, sampling, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);

                totalTokens += completion.Tokens;

                if (completion.Faulted)
                {
                    // The child died mid-generation, which is the whole reason it
                    // is a child. Start another and try again rather than handing
                    // the reader a crash: a fault that ends a process it does not
                    // own is a slow line, not a failed one.
                    reason = completion.Fault;

                    var replacement = await RestartAsync(cancellationToken).ConfigureAwait(false);

                    if (replacement is null)
                    {
                        break;
                    }

                    model = replacement;
                    continue;
                }

                var text = completion.Text;

                if (protect)
                {
                    if (!Engine.Markup.PlaceholderGuard.Holds(text, guards))
                    {
                        reason = "the placeholders did not come back intact";
                        continue;
                    }

                    text = Engine.Markup.PlaceholderGuard.Restore(text!, guards);
                }

                reason = Refuse(job, rag, sent, text, out var accepted);


                if (reason is null)
                {
                    LastGuardVerdict = null;
                    return new TranslationOutcome(accepted, totalTokens, clock.Elapsed);
                }
            }

            // The model would not carry the sentinels. Cut the text at its
            // placeholders instead and translate the prose between them: the
            // placeholders are never sent, so they cannot come back translated,
            // whatever the model does. It costs word order across a placeholder,
            // which is why it is here and not first.
            if (guards.Any)
            {
                var (spliced, spent) = await StructuredAsync(job, rag, Prompt, model, cancellationToken)
                    .ConfigureAwait(false);

                totalTokens += spent;

                if (spliced is not null)
                {
                    LastGuardVerdict = null;
                    return new TranslationOutcome(spliced, totalTokens, clock.Elapsed);
                }
            }

            // Exhausted. The caller sees no text, which is what an untranslated
            // entry means, and the reason is kept rather than swallowed.
            LastGuardVerdict = reason;
            return new TranslationOutcome(null, totalTokens, clock.Elapsed);
        }
        catch (BetterRuntimeException ex)
        {
            Fail(ex.Message);
            return TranslationOutcome.None(clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose. The entry keeps its source and says nothing
            // came back, which is what a cancelled run means.
            return TranslationOutcome.None(clock.Elapsed);
        }
        finally
        {
            _running = null;
            running.Dispose();
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// One unit of a document, through exactly the path a single send takes.
    ///
    /// It exists only to capture <see cref="LastGuardVerdict"/> alongside the
    /// answer. A document pass runs many units and has to report why lines were
    /// kept, and reading a property that the next unit overwrites would attribute
    /// each refusal to the wrong line.
    /// </summary>
    public async Task<UnitOutcome> TranslateUnitAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        var outcome = await TranslateAsync(job, cancellationToken).ConfigureAwait(false);

        return new UnitOutcome(outcome.Text, outcome.GeneratedTokens, LastGuardVerdict);
    }

    /// <summary>
    /// One completion from the resident model, with no translation prompt and
    /// none of the gates.
    ///
    /// It exists because the model is private and every other way in forces the
    /// translate instruction, then measures the answer against the source: for a
    /// chat title that is exactly wrong. A five-word title of a four-hundred
    /// character message is refused for collapsing to 8% of its length, and
    /// SlopValidator replaces it with the whole message for holding none of the
    /// message's placeholders. Neither guard is wrong -- the answer is simply not
    /// a translation, and the caller validates it as what it is.
    ///
    /// It takes the same gate the translate path takes, because the runtime
    /// refuses a second generation while one is live. So it must never be called
    /// from inside a translation.
    /// </summary>
    public async Task<string?> CompleteAsync(
        string modelPath,
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        if (!await EnsureLoadedAsync(modelPath, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_model is null)
            {
                return null;
            }

            var model = _model;

            var sampling = new GenParams
            {
                MaxTokens = maxTokens,
                Temperature = 0.2f,
                TopP = 0.95f,
                TopK = 40,
                RepeatPenalty = 1f,
                Seed = 0,
            };

            var completion = await Task
                .Run(() => model.CompleteCounted(prompt, sampling, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            // A chat name is a convenience. The runtime dying while producing one
            // is the caller's cue to keep the truncation, not something to raise.
            return completion.Faulted ? null : completion.Text;
        }
        catch (BetterRuntimeException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Translates a whole document, keeping its line count. Wrapped here so
    /// callers do not have to know that the guarded single-text path is what a
    /// document pass is built out of.
    /// </summary>
    public Task<DocumentTranslation> TranslateDocumentAsync(
        TranslationJob job,
        IProgress<DocumentProgress>? progress = null,
        int minLetters = Engine.Slop.TranslationCandidate.DefaultMinLetters,
        CancellationToken cancellationToken = default) =>
        new DocumentTranslator(TranslateUnitAsync) { MinLetters = minLetters }
            .TranslateAsync(job, progress, cancellationToken);

    /// <summary>
    /// True when a send should go through the document path rather than as one
    /// piece of text.
    ///
    /// The reason is not tidiness, it is blast radius. Ten lines sent as one
    /// blob are one answer that every gate judges as a whole, so a single bad
    /// line refuses all ten and the reader is shown ten untranslated sentences.
    /// Measured on exactly that: line by line, three of four came back correct;
    /// as one block, the whole thing was refused over one word.
    /// </summary>
    public static bool WantsDocumentPath(string? text) =>
        text is not null
        && text.ReplaceLineEndings("\n").Split('\n').Count(l => l.Trim().Length > 0) > 1;

    /// <summary>
    /// Stops a generation in progress. The model stays loaded, so the next send
    /// starts at once rather than paying the load again.
    ///
    /// True of the hosted path as well, which it once was not: the child is
    /// asked to stop and answers the request it stopped, rather than being left
    /// with a half-read pipe and killed for it. See
    /// <see cref="HostedSession.AnswerAsync"/>.
    /// </summary>
    public void CancelGeneration() => _running?.Cancel();

    /// <summary>
    /// Puts the model down and gives its memory back. There is no suspend in the
    /// runtime -- a loaded model is either resident or it is not -- so the honest
    /// pause is to unload it and reload on the next send.
    ///
    /// Cancels first: waiting for the gate while a generation holds it would
    /// hang the caller for as long as the answer takes.
    /// </summary>
    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        CancelGeneration();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _model?.Dispose();
            _model = null;
            _loadedPath = null;
            Set(TranslatorState.Idle, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One send plus one retry. The reference's own count: a second attempt is
    /// worth the seconds, a third is not -- a model that produced damage twice
    /// on the same prompt produces it again.
    /// </summary>
    private const int Attempts = 2;

    /// <summary>
    /// Translates the prose around the placeholders and puts the placeholders
    /// back where they were.
    ///
    /// The last line of defence for a placeholder, and the only one that does not
    /// depend on the model doing anything: a token that was never sent cannot
    /// come back translated. Each prose run is judged only on whether an answer
    /// arrived and is not the run itself -- the full gate stack is calibrated for
    /// whole units and would refuse "beside the input unless" for being short.
    /// The assembled result is then put through every gate, which is the text the
    /// reader would actually receive.
    ///
    /// Returns null when the assembled text still fails, so the caller reports
    /// the original refusal rather than a worse answer.
    /// </summary>
    private static async Task<(string? Text, int Tokens)> StructuredAsync(
        TranslationJob job,
        Engine.Slop.RagConfig rag,
        Func<string, string> prompt,
        IInferenceSession model,
        CancellationToken cancellationToken)
    {
        var runs = Engine.Markup.PlaceholderGuard.Split(job.Text);

        if (runs.Count(Engine.Markup.PlaceholderGuard.IsProse) == 0)
        {
            return (null, 0);
        }

        var built = new System.Text.StringBuilder(job.Text.Length);
        var tokens = 0;

        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Engine.Markup.PlaceholderGuard.IsProse(run))
            {
                built.Append(run.Text);
                continue;
            }

            // The run without the spaces that joined it to its neighbours, so the
            // model is handed a phrase and the spacing is restored around it.
            var trimmed = run.Text.Trim();
            var lead = run.Text[..run.Text.IndexOf(trimmed[0], StringComparison.Ordinal)];
            var tail = run.Text[(run.Text.LastIndexOf(trimmed[^1]) + 1)..];

            var completion = await Task
                .Run(() => model.CompleteCounted(prompt(trimmed), job.Sampling(0), cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            tokens += completion.Tokens;

            if (completion.Faulted)
            {
                return (null, tokens);
            }

            var cleaned = Engine.Markup.ChunkIntegrity.RemoveTranslatorNote(trimmed, completion.Text);
            cleaned = Engine.Markup.ChunkIntegrity.DropInventedCodeSpans(trimmed, cleaned).Trim();

            if (cleaned.Length == 0 || string.Equals(cleaned, trimmed, StringComparison.Ordinal))
            {
                return (null, tokens);
            }

            built.Append(lead).Append(cleaned).Append(tail);
        }

        var assembled = built.ToString();

        return (Refuse(job, rag, prompt(job.Text), assembled, out var accepted) is null ? accepted : null, tokens);
    }

    /// <summary>
    /// Every gate, in the order `translate-master.ps1` runs them, returning the
    /// reason to refuse or null to accept.
    ///
    /// The order is not arbitrary. A translator's note is stripped first, so a
    /// good answer is not thrown away over a trailing annotation. The leak gate
    /// runs before the repairs, because an answer that narrates the task is not
    /// worth repairing. The slop validator then repairs what it can, and the
    /// structural and glossary gates judge the repaired text -- which is what
    /// the document would actually receive.
    /// </summary>
    private static string? Refuse(
        TranslationJob job,
        Engine.Slop.RagConfig rag,
        string prompt,
        string? answer,
        out string accepted)
    {
        accepted = string.Empty;

        if (string.IsNullOrWhiteSpace(answer))
        {
            return "no answer";
        }

        var cleaned = Engine.Markup.ChunkIntegrity.RemoveTranslatorNote(job.Text, answer);

        // Chat text only, alongside the other two standalone relaxations: a
        // document line's backticks are its own markup and adding to them there
        // would change what the file renders as.
        if (job.IsStandalone)
        {
            cleaned = Engine.Markup.ChunkIntegrity.DropInventedCodeSpans(job.Text, cleaned);
        }

        // The prompt is passed so the leak check can be generated from it rather
        // than from a fixed blacklist -- which keeps working now that the prompt
        // is editable from Settings.
        var leak = Engine.Markup.OutputLeak.Check(
            job.Text,
            cleaned,
            prompt,
            PipelineMaxLengthRatio,
            job.IsStandalone ? StandaloneWordFloor : Engine.Markup.OutputLeak.DefaultProportionalWordFloor);

        if (leak is not null)
        {
            return "leak: " + leak;
        }

        var slop = Engine.Slop.SlopValidator.Validate(job.Text, cleaned, rag.Slop);

        if (slop.Verdict == Engine.Slop.SlopVerdict.Integrity)
        {
            return "placeholder damage";
        }

        var integrity = Engine.Markup.ChunkIntegrity.Check(
            job.Text, slop.Text, Terms.Strict, Terms.Declinable, PipelineMinLengthRatio, job.IsStandalone);

        if (integrity is not null)
        {
            return integrity;
        }

        // Project vocabulary, and only where there is a project. The shipped
        // glossary requires "store" to become "úložiště" -- correct for a model
        // store, and wrong for the shop Mr. White went to last night. Applied to
        // general prose it refuses correct translations, and a refusal keeps the
        // source, so the reader sees untranslated English.
        if (job.UseProjectVocabulary)
        {
            var glossary = Engine.Slop.Glossary.Check(rag.Glossary, job.Text, slop.Text);

            if (glossary is not null)
            {
                return glossary;
            }
        }

        // An answer identical to its source was not translated -- but only where
        // the source had something that HAD to change. "OK", "Linux", "PDF" and
        // a line of pure markup are correct unchanged, and refusing them costs a
        // retry and then reports a failure for the right answer.
        //
        // Asked with the same predicate that decides whether to send a line at
        // all: if there was nothing here worth translating, an identical answer
        // is not evidence the model ignored the request.
        if (string.Equals(slop.Text.Trim(), job.Text.Trim(), StringComparison.Ordinal)
            && Engine.Slop.TranslationCandidate.EchoIsDefect(job.Text, Terms.Strict))
        {
            return "unchanged";
        }

        // Last, and after every gate has judged the model's own answer: casing is
        // a property of the source rather than a translation decision, and the
        // model drops it unpredictably -- `PROJECT:` survives, `FEATURE:` comes
        // back as `Funkce:`. Restored here so every caller gets it, including
        // the document path.
        var cased = Engine.Markup.CasingGuard.Restore(job.Text, slop.Text);

        // After every gate, because this corrects rather than refuses. A gate
        // that rejected `motor` would cost the reader the whole line and tell
        // them nothing; one wrong word in an otherwise good sentence is worth
        // keeping and fixing.
        var corrected = job.UseDomainVocabulary
            ? Engine.Terminology.TerminologyCorrector
                .Apply(job.Text, cased, Engine.Terminology.DomainTerms.For(job.To.Code))
                .Text
            : cased;

        accepted = Engine.Markup.ByteHygiene.DropInventedMarkup(
            job.Text,
            Engine.Markup.ByteHygiene.Restore(job.Text, corrected));

        return null;
    }

    /// <summary>
    /// The pipeline's own thresholds, looser than the gates' defaults. A single
    /// composer line is shorter and more variable than a document chunk, and the
    /// reference widens both bounds at this call site for exactly that reason.
    /// </summary>
    private const double PipelineMaxLengthRatio = 2.4;

    private const double PipelineMinLengthRatio = 0.35;

    /// <summary>
    /// Where the word ceiling turns proportional for a standalone sentence.
    ///
    /// Twenty rather than the document figure of eight. Between the two, Czech
    /// routinely runs longer than its English source because a heading like
    /// "FEATURE: JSON-aware mode for the same message textbox" carries terms it
    /// has to spell out. Past twenty the proportional rule is back, because a
    /// long answer that grew by a third is where invention actually shows.
    /// </summary>
    private const int StandaloneWordFloor = 20;

    private bool Fail(string reason)
    {
        Set(TranslatorState.Failed, reason);
        return false;
    }

    private void Set(TranslatorState state, string? reason)
    {
        State = state;
        Reason = reason;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
        _gate.Dispose();
    }
}
