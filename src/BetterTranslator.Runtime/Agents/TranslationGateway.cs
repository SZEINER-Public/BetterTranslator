using System.Text;
using BetterTranslator.Core.Languages;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Json;
using BetterTranslator.Engine.Languages;
using BetterTranslator.Engine.Markdown;
using BetterTranslator.Engine.Verification;
using BetterTranslator.Runtime.Downloads;
using BetterTranslator.Runtime.Inference;
using BetterTranslator.Runtime.Models;

namespace BetterTranslator.Runtime.Agents;

public sealed class TranslationGateway : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Database _database;
    private readonly SettingsStore _settingsStore;
    private readonly InstallPaths _installPaths;
    private readonly ITranslationEngine _engine;

    private readonly VerificationPipeline _pipeline;

    private readonly ChatWriter _chats;

    /// <summary>
    /// This session's chat, created when it first has something to put in it.
    /// One per gateway: a run of the server, or one invocation of bt. Lazily,
    /// because a session that only lists languages should leave nothing behind.
    /// </summary>
    private Guid? _chatId;

    /// <summary>
    /// The window, when this gateway is running inside one. Set by the host that
    /// owns both; null for bt, which has no window to tell.
    /// </summary>
    public Mcp.IGuiBridge? Gui { get; set; }
    private readonly LanguageRegistry _registry = new();
    private readonly DirectionGuard _guard = new();

    private AppSettings _settings;

    public TranslationGateway(
        Database database,
        SettingsStore settingsStore,
        InstallPaths installPaths,
        AppSettings settings,
        ITranslationEngine engine,
        VerificationPipeline? pipeline = null)
    {
        _database = database;
        _settingsStore = settingsStore;
        _installPaths = installPaths;
        _settings = settings;
        _engine = engine;

        // The same pass the window runs over what came back, built from the same
        // stored settings and finding its dictionary the same way. Null when
        // verification is switched off or no dictionary for the target language
        // is on this machine, which is not a failure.
        _pipeline = pipeline ?? VerificationFactory.CreatePipeline(
            settings.Verification,
            _registry.Resolve(settings.TargetLanguage)?.Code,
            new AppPaths().DictionariesFolder,
            semantics: _ => (_engine as LocalTranslationEngine)?.Translator is { Session: { IsAlive: true } session, LastJob: { } template }
                ? Verification.SemanticRuntime.Services(_installPaths, session, template)
                : null);
        _chats = new ChatWriter(new ChatStore(database));

        Jobs = JobRegistry.Shared;
    }

    /// <summary>
    /// Opens the gateway.
    ///
    /// <paramref name="ownsRuntime"/> says whether this gateway is the process's
    /// only translator. In <c>bt</c> it is, and the stored backend has to be
    /// applied because nothing else will. Inside the application it is not: the
    /// window resolved the backend at startup and deliberately keeps running the
    /// one it loaded until a restart, so an agent server starting up must not
    /// reach into that. It is process-wide state -- which flavour of the native
    /// library loads -- and applying a newer setting here changed the window's
    /// runtime out from under it, without the restart it asks for.
    /// </summary>
    public VerificationPipeline Pipeline => _pipeline;

    public static async Task<TranslationGateway> StartAsync(
        CancellationToken cancellationToken,
        bool ownsRuntime = true,
        VerificationPipeline? pipeline = null)
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        Engine.Config.ConfigStore.UseFolder(paths.ConfigFolder);

        var database = new Database(paths);
        await database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var settingsStore = new SettingsStore(database);
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var installPaths = new InstallPaths(paths);

        // Additive either way: a downloaded flavour lives in the models folder,
        // and telling the loader to look there takes nothing away from anyone.
        BackendCatalog.SearchAlso(installPaths.ModelsFolder);

        ApplyBackend(settings, ownsRuntime);

        var gateway = new TranslationGateway(database, settingsStore, installPaths, settings, new LocalTranslationEngine(), pipeline);

        // The same index the window's Memory screen reads, loaded the same way.
        // Failing to load it is not a reason to refuse to translate: an empty
        // index and an unreadable one both mean no passages, which is what a
        // translation without the project's knowledge already is.
        try
        {
            var memory = new Indexing.Retrieval.MemoryService();
            await memory.LoadFromAsync(new Indexing.Index.IndexStore(database), cancellationToken).ConfigureAwait(false);
            gateway._memory = memory;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }

        return gateway;
    }

    /// <summary>
    /// Applies the stored backend, or declines to. Separated from
    /// <see cref="StartAsync"/> so the decision can be exercised without opening
    /// a gateway over the real data folder, which is what a test of it would
    /// otherwise have to do.
    /// </summary>
    internal static bool ApplyBackend(AppSettings settings, bool ownsRuntime)
    {
        if (!ownsRuntime)
        {
            return false;
        }

        LocalTranslator.Prefer(new BackendCatalog().Resolve(settings.RuntimeBackend));
        return true;
    }

    public JobRegistry Jobs { get; }

    public AppSettings Settings => _settings;

    public string ModelPath => ResolveModelPath();

    public string ModelName
    {
        get
        {
            var path = ModelPath;
            return path.Length == 0 ? string.Empty : Path.GetFileNameWithoutExtension(path);
        }
    }

    public string ModelId => Path.GetFileName(ModelPath);

    public IReadOnlyList<LanguageToolRow> Languages() => LanguageTools.List(ModelId);

    public IReadOnlyList<ModelSummary> Models()
    {
        var selected = ModelPath;
        var library = new ModelLibrary();
        var found = library.Scan([.. ModelLibrary.DefaultFolders(_installPaths.ModelsFolder)]);
        var summaries = new List<ModelSummary>();

        foreach (var component in ComponentCatalog.BuiltIn.Where(c => c.Kind == ComponentKind.Model))
        {
            var local = library.Match(found, component.FileName);

            summaries.Add(new ModelSummary(
                component.Name,
                component.FileName,
                local?.Path ?? string.Empty,
                local?.SizeBytes ?? 0,
                local is not null,
                local is not null && string.Equals(local.Path, selected, StringComparison.OrdinalIgnoreCase),
                LanguageRegistry.ModelFlag(component.FileName)));
        }

        foreach (var local in found)
        {
            if (summaries.Any(s => string.Equals(s.Path, local.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            summaries.Add(new ModelSummary(
                local.Name,
                Path.GetFileName(local.Path),
                local.Path,
                local.SizeBytes,
                true,
                string.Equals(local.Path, selected, StringComparison.OrdinalIgnoreCase),
                LanguageRegistry.ModelFlag(local.Path)));
        }

        return summaries;
    }

    public async Task<(ModelSummary? Model, AgentError? Error)> SelectModelAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var wanted = name?.Trim() ?? string.Empty;

        if (wanted.Length == 0)
        {
            return (null, new AgentError(AgentFault.NotFound, "Name a model. Call list_models to see what is installed."));
        }

        var models = Models();
        var match = models.FirstOrDefault(m => m.Installed
                && (string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.FileName, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(m.Path, wanted, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            var installed = string.Join(", ", models.Where(m => m.Installed).Select(m => m.Name));

            return (null, new AgentError(
                AgentFault.NotFound,
                installed.Length == 0
                    ? $"No model called '{wanted}' is installed, and no model is installed at all. Install one from the app's Downloads screen."
                    : $"No installed model called '{wanted}'. Installed models: {installed}."));
        }

        _settings.SelectedModelPath = match.Path;
        _settings.SelectedModelFile = ModelSelection.FileNameOf(match.Path);
        _settings.ModelChosenExplicitly = true;
        _settings.Effort = EffortTiers.ForModelId(LanguageRegistry.ModelFlag(match.FileName))?.Effort
            ?? _settings.Effort;

        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);

        return (match with { Selected = true }, null);
    }

    public async Task<EntrySummary?> GetEntryAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await new ChatStore(_database).GetEntryAsync(id, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return null;
        }

        return new EntrySummary(
            entry.Id.ToString(),
            entry.ChatId.ToString(),
            entry.Kind.ToString(),
            entry.State.ToString(),
            entry.Source,
            entry.Result,
            entry.TargetLanguage,
            entry.CreatedAt.ToString("O"),
            entry.FileName,
            entry.GeneratedTokens,
            entry.DurationMs);
    }

    public const string DefaultSourceLanguage = "en";

    public (TranslationDirection? Direction, AgentError? Error) Direction(string? from, string? to)
    {
        var source = _registry.Resolve(string.IsNullOrWhiteSpace(from) ? DefaultSourceLanguage : from);

        if (source is null)
        {
            return (null, Unknown(from, "source"));
        }

        var target = _registry.Resolve(to);

        if (target is null)
        {
            return (null, Unknown(to, "target"));
        }

        var direction = TranslationDirection.Of(
            LanguageChoice.Of(source.Code, source.Name, source.Script),
            LanguageChoice.Of(target.Code, target.Name, target.Script));

        var verdict = _guard.Inspect(direction, ModelId, ModelName);

        return verdict.Status switch
        {
            DirectionStatus.Ready => (direction, null),
            DirectionStatus.SameLanguage => (null, new AgentError(
                AgentFault.SameLanguage,
                $"Source and target are both {target.Name}. Nothing would be translated; choose a different target.")),
            DirectionStatus.SourceUnsupported => (null, new AgentError(
                AgentFault.ModelDoesNotSupportLanguage,
                $"{ModelName} does not support {source.Name} as a source language. Call list_models and select_model to switch, or call list_languages for what this model can do.")),
            DirectionStatus.TargetUnsupported => (null, new AgentError(
                AgentFault.ModelDoesNotSupportLanguage,
                $"{ModelName} does not support {target.Name} as a target language. Call list_models and select_model to switch, or call list_languages for what this model can do.")),
            _ => (direction, null),
        };
    }

    /// <summary>
    /// <paramref name="usesMemory"/> is the composer's Memory chip, for a caller
    /// that has no composer: it brings this project's indexed passages and its
    /// glossary into the send. Off by default, exactly as the chip is, because a
    /// glossary is a claim about one body of documents and applying it to general
    /// prose refuses correct translations.
    /// </summary>
    public async Task<TextTranslation> TranslateTextAsync(
        string text,
        string? from,
        string? to,
        bool usesMemory,
        CancellationToken cancellationToken)
    {
        var model = ModelPath;

        if (model.Length == 0)
        {
            return TextTranslation.Failed(from ?? string.Empty, to ?? string.Empty, string.Empty, NoModel());
        }

        var (direction, error) = Direction(from, to);

        if (direction is null)
        {
            return TextTranslation.Failed(from ?? string.Empty, to ?? string.Empty, ModelName, error!);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return TextTranslation.Failed(
                direction.Source.Code.Value,
                direction.Target.Code.Value,
                ModelName,
                new AgentError(AgentFault.TranslationFailed, "There is no text to translate."));
        }

        var entry = await BeginEntryAsync(text, direction, file: null, cancellationToken).ConfigureAwait(false);

        var run = await RunContentAsync(text, direction, model, usesMemory, progress: null, cancellationToken)
            .ConfigureAwait(false);

        await FinishEntryAsync(entry, run.Content.Text, run.Tokens, run.DurationMs, cancellationToken)
            .ConfigureAwait(false);

        if (run.Content.Text is null)
        {
            return TextTranslation.Failed(
                direction.Source.Code.Value,
                direction.Target.Code.Value,
                ModelName,
                Refused(run.Content.Refusal ?? _lastRefusal));
        }

        return new TextTranslation(
            direction.Source.Code.Value,
            direction.Target.Code.Value,
            ModelName,
            run.Content.Text,
            run.Tokens,
            run.DurationMs,
            null)
        {
            Note = run.Note,
            EntryId = entry?.Id.ToString(),
            Verification = VerificationSummary.From(run.Verification),
        };
    }

    public async Task<FileTranslation> TranslateFileAsync(
        string input,
        string? output,
        string? from,
        string? to,
        bool overwrite,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(input);

        if (!File.Exists(full))
        {
            return FileTranslation.Failed(full, new AgentError(
                AgentFault.InputMissing,
                $"There is no file at {full}. Give a path that exists."));
        }

        var format = DocumentFormats.Of(full);

        if (format == DocumentFormat.Unsupported)
        {
            return FileTranslation.Failed(full, new AgentError(
                AgentFault.UnsupportedFormat,
                DocumentFormats.UnsupportedMessage(full)));
        }

        var model = ModelPath;

        if (model.Length == 0)
        {
            return FileTranslation.Failed(full, NoModel());
        }

        var (direction, error) = Direction(from, to);

        if (direction is null)
        {
            return FileTranslation.Failed(full, error!);
        }

        var destination = DocumentFormats.OutputPathFor(full, direction.Target.Code.Value, output);

        if (File.Exists(destination) && !overwrite)
        {
            return FileTranslation.Failed(full, new AgentError(
                AgentFault.OutputExists,
                $"{destination} already exists. Pass a different output path, or set overwrite to true."));
        }

        var (source, unreadable) = await ReadAsync(full, cancellationToken).ConfigureAwait(false);

        if (unreadable is not null)
        {
            return FileTranslation.Failed(full, unreadable);
        }

        // A file is translated against the project it belongs to, which is what
        // the window does with a file too: there is no composer here to attach a
        // chip, and the glossary is a claim about exactly this body of documents.
        var entry = await BeginEntryAsync(
            source,
            direction,
            new FileEntry(full, Path.GetFileName(full), new FileInfo(full).Length),
            cancellationToken).ConfigureAwait(false);

        var run = await RunContentAsync(source, direction, model, usesMemory: true, progress, cancellationToken)
            .ConfigureAwait(false);

        await FinishEntryAsync(entry, run.Content.Text, run.Tokens, run.DurationMs, cancellationToken)
            .ConfigureAwait(false);

        if (run.Content.Text is null)
        {
            return FileTranslation.Failed(full, Refused(run.Content.Refusal ?? _lastRefusal));
        }

        try
        {
            var folder = Path.GetDirectoryName(destination);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            await File.WriteAllTextAsync(destination, run.Content.Text, Utf8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileTranslation.Failed(full, new AgentError(AgentFault.TranslationFailed, ex.Message));
        }

        return FileTranslation.Done(full, destination) with { Note = run.Note, Verification = VerificationSummary.From(run.Verification) };
    }

    public IReadOnlyList<string> BatchFiles(string folder) =>
        !Directory.Exists(folder)
            ? []
            : [.. Directory.EnumerateFiles(folder).Where(DocumentFormats.IsTranslatable).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

    public async Task<IReadOnlyList<FileTranslation>> TranslateBatchAsync(
        string folder,
        string? outputFolder,
        string? from,
        string? to,
        bool overwrite,
        IProgress<FileTranslation>? each,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(folder);

        if (!Directory.Exists(full))
        {
            return [FileTranslation.Failed(full, new AgentError(
                AgentFault.InputMissing,
                $"There is no folder at {full}. Give a folder that exists."))];
        }

        var files = BatchFiles(full);

        if (files.Count == 0)
        {
            return [];
        }

        var (direction, error) = Direction(from, to);

        if (direction is null)
        {
            var refused = files.Select(f => FileTranslation.Failed(f, error!)).ToList();

            foreach (var one in refused)
            {
                each?.Report(one);
            }

            return refused;
        }

        var code = direction.Target.Code.Value;
        var results = new List<FileTranslation>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = string.IsNullOrWhiteSpace(outputFolder)
                ? null
                : Path.Combine(
                    Path.GetFullPath(outputFolder),
                    Path.GetFileName(DocumentFormats.OutputPathFor(file, code, null)));

            var result = await TranslateFileAsync(file, destination, from, to, overwrite, null, cancellationToken)
                .ConfigureAwait(false);

            results.Add(result);
            each?.Report(result);
        }

        return results;
    }

    /// <summary>
    /// What one run of the shared pipeline produced, and what it cost. The cost
    /// is added up here because a document is many calls and every surface
    /// reports one figure.
    /// </summary>
    private sealed record ContentRun(ContentTranslationResult Content, int Tokens, TimeSpan Elapsed)
    {
        /// <summary>Spans the verifier flagged across every unit of this run.</summary>
        public int Flagged { get; init; }

        public Core.Verification.VerificationResult? Verification { get; init; }

        public int DurationMs => (int)Elapsed.TotalMilliseconds;

        /// <summary>
        /// What the window would have shown: what the reassembly did to the
        /// text, and what checking it afterwards found. The window draws the
        /// findings under the words; a caller with no window is told how many.
        /// </summary>
        public string? Note
        {
            get
            {
                string?[] parts =
                [
                    Content.Note,
                    Flagged switch
                    {
                        0 => null,
                        1 => "verification flagged 1 span",
                        _ => $"verification flagged {Flagged} spans",
                    },
                ];

                var said = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

                return said.Length == 0 ? null : string.Join("; ", said);
            }
        }
    }

    /// <summary>
    /// The one path a translation takes on this surface, and the same one the
    /// window takes: routed by content, cut by shape, one call per unit, with
    /// whatever context the caller asked for travelling on every one of them.
    ///
    /// There is no separate "text" and "file" behaviour any more. A file is text
    /// that came off a disk.
    /// </summary>
    private async Task<ContentRun> RunContentAsync(
        string source,
        TranslationDirection direction,
        string model,
        bool usesMemory,
        IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        _lastRefusal = null;

        var tokens = 0;
        var elapsed = TimeSpan.Zero;
        var units = 0;
        var flagged = 0;

        var content = await ContentTranslation.TranslateAsync(
            source,
            async (unit, token) =>
            {
                var job = JobFor(
                    unit,
                    direction,
                    model,
                    standalone: true,
                    memory: MemoryFor(unit, usesMemory),
                    usesMemory: usesMemory);

                var answer = await _engine.TranslateAsync(job, token).ConfigureAwait(false);

                if (!answer.HasText)
                {
                    _lastRefusal = answer.Refusal;
                }

                // Checked here, one unit at a time, because that is where the
                // window checks it: the verifier compares a source against its
                // translation, and after reassembly there is no longer a pair to
                // compare -- only a document beside a document.
                if (_pipeline.HasVerifier && answer.HasText)
                {
                    var trace = new Engine.Verification.Structure.SegmentTrace(0, unit.Length, Core.Verification.Checks.SegmentOutcome.Translated, answer.Text, null, answer.Text, 0, answer.Text!.Length);
                    var verified = _pipeline.Verify(unit, answer.Text!, [trace], direction.Source.Code.Value, direction.Target.Code.Value);

                    if (verified.Executed)
                    {
                        flagged += verified.Spans.Count(s => !s.Exempt && s.Tier != Core.Verification.SeverityTier.Clean);
                    }
                }

                tokens += answer.GeneratedTokens;
                elapsed += answer.Duration;

                units++;
                progress?.Report(new DocumentProgress { Activity = "translating", UnitsDone = units });

                return answer.Text;
            },
            cancellationToken)
            .ConfigureAwait(false);

        var verification = content.Text is { Length: > 0 }
            ? _pipeline.Verify(source, content.Text, content.Segments, direction.Source.Code.Value, direction.Target.Code.Value)
            : null;

        return new ContentRun(content, tokens, elapsed) { Flagged = flagged, Verification = verification };
    }

    /// <summary>
    /// The text of a file, however it has to be got out of it.
    ///
    /// A PDF or a Word document goes through the same readers the window uses,
    /// so both surfaces translate the same words. Everything else is read as
    /// what it is: the readers cover four families, and a .json or a .csv has no
    /// reader and needs none.
    ///
    /// Extraction fails in a way a byte check cannot see. A scanned PDF has no
    /// text layer and yields nothing, or glyph soup, and translating that and
    /// reporting "ok" would be worse than refusing it -- so the extracted
    /// formats go through the same quality gate the indexer applies.
    /// </summary>
    private static async Task<(string Text, AgentError? Error)> ReadAsync(
        string full,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!DocumentFormats.NeedsReader(full))
            {
                var text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);

                return DocumentFormats.LooksBinary(text)
                    ? (string.Empty, new AgentError(AgentFault.UnsupportedFormat, DocumentFormats.UnsupportedMessage(full)))
                    : (text, null);
            }

            var content = await new Indexing.Readers.DocumentReaders()
                .ReadAsync(full, cancellationToken)
                .ConfigureAwait(false);

            if (Engine.Corpus.ExtractedText.RejectionReason(content.Text) is { } reason)
            {
                return (string.Empty, new AgentError(
                    AgentFault.UnsupportedFormat,
                    $"{Path.GetFileName(full)} holds no text that could be read: {reason}. "
                    + "A scanned document has to be put through optical recognition before it can be translated."));
            }

            return (content.Text, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever a reader throws at a file it cannot make sense of stops
            // this file, not the batch it is part of.
            return (string.Empty, new AgentError(AgentFault.InputMissing, ex.Message));
        }
    }

    /// <summary>
    /// Puts the row on the surface before the model is asked, as the window
    /// does, so what was asked for survives whatever happens next.
    ///
    /// A row is not worth failing a translation over. A database that will not
    /// take one still hands back the translation; the caller simply gets no
    /// entry id, and nothing appears in the window.
    /// </summary>
    private async Task<Entry?> BeginEntryAsync(
        string source,
        TranslationDirection direction,
        FileEntry? file,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.Now;

            _chatId ??= (await _chats.StartChatAsync(source, now, cancellationToken).ConfigureAwait(false)).Id;

            return await _chats.BeginAsync(_chatId.Value, source, direction, now, file, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private async Task FinishEntryAsync(
        Entry? entry,
        string? result,
        int tokens,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            await _chats.FinishAsync(entry, result, tokens, durationMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return;
        }

        await NameChatAsync(entry, cancellationToken).ConfigureAwait(false);

        // The window has no way of noticing a row it did not write itself.
        if (Gui is not null)
        {
            await Gui.EntryWrittenAsync(entry.ChatId, entry.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The model that just translated names the chat, which is what the window
    /// does with its first entry: the same resident model, so nothing is loaded
    /// and nothing is downloaded for a chat name.
    ///
    /// Only the first landed translation of a session, and only while the name
    /// is still the truncated source. Naming is a convenience -- every failure
    /// is silent and leaves the truncation, which reads perfectly well.
    /// </summary>
    private async Task NameChatAsync(Entry entry, CancellationToken cancellationToken)
    {
        if (_named || entry.Result.Length == 0)
        {
            return;
        }

        _named = true;

        try
        {
            var store = new ChatStore(_database);
            var chat = (await store.GetChatsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(c => c.Id == entry.ChatId);

            if (chat is null || !chat.NameIsProvisional)
            {
                return;
            }

            var namer = new ChatNamer(_engine.CompleteAsync, _engine.TranslateRawAsync);

            var title = await namer.NameAsync(
                new ChatNameRequest
                {
                    Source = entry.Source,
                    ModelPath = ModelPath,
                    Direction = DirectionOf(entry),
                    Provisional = chat.Name,
                },
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(title))
            {
                await store.RenameChatAsync(chat.Id, title, provisional: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
        }
    }

    private static TranslationDirection DirectionOf(Entry entry) =>
        TranslationDirection.Between(entry.SourceCode, entry.SourceCode, entry.TargetCode, entry.TargetLanguage);

    private bool _named;

    private string? _lastRefusal;

    /// <summary>
    /// The retrieved context for one unit, or null when this project's knowledge
    /// was not asked for. Built per unit, as the window builds it: the phrase
    /// retrieval answers is the unit, so a ten-line message performs ten lookups
    /// and each line is grounded in what that line is about.
    /// </summary>
    private string? MemoryFor(string unit, bool usesMemory)
    {
        if (!usesMemory || _memory is null)
        {
            return null;
        }

        var lookup = _memory.Lookup(unit, queryVector: null, _settings.UnsureThresholdPercent);

        // No pairs: those are a chat's own earlier translations and an agent
        // call has no chat behind it. The passages are the project half, and
        // they are the half a file or a standalone request can have.
        return Indexing.Retrieval.MemoryContext.Build([], _memory.Passages(lookup.Hits));
    }

    /// <summary>
    /// The project index, loaded once when the gateway opens and read-only after
    /// that. Once, because a lookup that re-read every chunk from the database
    /// would do a full table scan per unit; read-only, because this surface has
    /// no way to index anything and several tool calls can be in flight at once.
    /// </summary>
    private Indexing.Retrieval.MemoryService? _memory;

    /// <summary>
    /// Through the shared factory, so an agent's job carries what the window's
    /// job carries: the stored temperature, the standing instruction, the
    /// project's vocabulary when its knowledge was asked for. Filling the record
    /// here by hand is how those four came to be missing.
    /// </summary>
    private TranslationJob JobFor(
        string text,
        TranslationDirection direction,
        string model,
        bool standalone,
        string? memory = null,
        bool usesMemory = false) =>
        TranslationJobs.For(
            new TranslationRequest(text, direction, model)
            {
                Memory = memory,
                UsesMemory = usesMemory,
                IsStandalone = standalone,
            },
            _settings);

    private AgentError Unknown(string? given, string side) => new(
        AgentFault.UnknownLanguage,
        string.IsNullOrWhiteSpace(given)
            ? $"No {side} language was given. Pass a code such as en or cs; call list_languages for the full set."
            : $"'{given}' is not a language this registry knows, so it cannot be the {side}. Call list_languages for the codes and names that are accepted.");

    private static AgentError NoModel() => new(
        AgentFault.ModelMissing,
        "No translation model is installed, so nothing can be translated. Install one from the app's Downloads screen, then call list_models.");

    private static AgentError Refused(string? verdict) => new(
        string.IsNullOrWhiteSpace(verdict) ? AgentFault.RuntimeUnreachable : AgentFault.TranslationFailed,
        string.IsNullOrWhiteSpace(verdict)
            ? "The local model runtime produced nothing. Check that the runtime library is installed and that the model file is readable, then try again."
            : verdict);

    private string ResolveModelPath()
    {
        var library = new ModelLibrary();
        var found = library.Scan([.. ModelLibrary.DefaultFolders(_installPaths.ModelsFolder)]);

        var component = ComponentCatalog.BuiltIn.FirstOrDefault(c =>
            c.Id == EffortTiers.For(_settings.Effort).ModelId);

        return ModelSelection.Resolve(
            library,
            found,
            _settings.SelectedModelFile,
            _settings.SelectedModelPath,
            _settings.ModelChosenExplicitly,
            component?.FileName ?? string.Empty).Path;
    }

    public void Dispose() => _engine.Dispose();
}
