using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BetterTranslator.Runtime.Agents.Mcp;

public static class BetterTranslatorTools
{
    public static IReadOnlyList<string> Names =>
    [
        "translate_text",
        "translate_file",
        "translate_batch",
        "job_status",
        "job_cancel",
        "list_languages",
        "list_models",
        "select_model",
        "show_in_gui",
        "get_entry",
    ];

    public static IReadOnlyList<McpServerTool> Create(TranslationGateway gateway, IGuiBridge gui) =>
    [
        TranslateText(gateway),
        TranslateFile(gateway),
        TranslateBatch(gateway),
        JobStatus(gateway),
        JobCancel(gateway),
        ListLanguages(gateway),
        ListModels(gateway),
        SelectModel(gateway, gui),
        ShowInGui(gui),
        GetEntry(gateway),
    ];

    private static McpServerTool Tool(Delegate handler, McpServerToolCreateOptions options)
    {
        var tool = McpServerTool.Create(handler, options);

        tool.ProtocolTool.OutputSchema = options.OutputSchema;

        return tool;
    }

    private static McpServerTool TranslateText(TranslationGateway gateway) => Tool(
        async (
            [Description("The text to translate. Send it exactly as it should be read, including punctuation and line breaks.")] string text,
            [Description("Language the text is written in, as a code such as en, cs, de or zh-CN, or its English name. Call list_languages for the accepted set.")] string from,
            [Description("Language to translate into, as a code such as cs, de or zh-CN, or its English name. Must differ from 'from'.")] string to,
            [Description("Translate against this project's indexed documents and its glossary, the way the application window does when its Memory chip is attached. False by default: a glossary is a claim about one body of documents, and applied to general prose it refuses correct translations.")] bool? use_memory = null,
            CancellationToken cancellationToken = default) =>
        {
            var result = await gateway.TranslateTextAsync(text, from, to, use_memory ?? false, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Ok)
            {
                return McpPayload.Error(result.Error!.Message);
            }

            return McpPayload.Ok(
                new
                {
                    ok = true,
                    from = result.From,
                    to = result.To,
                    model = result.Model,
                    result = result.Text,
                    generated_tokens = result.GeneratedTokens,
                    duration_ms = result.DurationMs,
                    note = result.Note,
                    entry_id = result.EntryId,
                },
                McpPayload.Pairs(
                [
                    ("from", result.From),
                    ("to", result.To),
                    ("model", result.Model),
                    ("result", result.Text),
                    .. result.Note is null ? Array.Empty<(string, string)>() : [("note", result.Note)],
                ]));
        },
        new McpServerToolCreateOptions
        {
            Name = "translate_text",
            Title = "Translate text",
            Description =
                "Translate a string with the local model and return the translation. "
                + "Use for a sentence, a paragraph or a short document held in memory. "
                + "For a file on disk use translate_file, and for a folder use translate_batch.",
            ReadOnly = true,
            Destructive = false,
            Idempotent = false,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["ok", "from", "to", "model", "result"],
                  "properties": {
                    "ok": { "type": "boolean", "description": "True when a translation came back." },
                    "from": { "type": "string", "description": "Canonical source language code." },
                    "to": { "type": "string", "description": "Canonical target language code." },
                    "model": { "type": "string", "description": "Model that produced the translation." },
                    "result": { "type": "string", "description": "The translated text." },
                    "generated_tokens": { "type": "integer", "description": "Tokens the model generated." },
                    "duration_ms": { "type": "integer", "description": "How long the translation took." },
                    "note": { "type": ["string", "null"], "description": "What the reader would otherwise have to spot by comparing the two texts: lines that kept their source, blocks retranslated phrase by phrase, spans the verifier flagged. Null when the run was uneventful." },
                    "entry_id": { "type": ["string", "null"], "description": "The stored entry this translation became, in the application's own history. Pass it to get_entry to read it back, or to show_in_gui to reveal it in the window." }
                  }
                }
                """),
        });

    private static McpServerTool TranslateFile(TranslationGateway gateway) => Tool(
        (
            [Description("Absolute path of the file to translate. .md keeps its structure, .json keeps its keys, other text keeps its line count. .pdf and .docx are read with the same readers the application window uses and written back as text, named <name>.<ext>.<to>.txt, because neither format can be written back.")] string path,
            [Description("Language to translate into, as a code such as cs or de, or its English name.")] string to,
            [Description("Language the file is written in. Defaults to en when omitted.")] string? from,
            [Description("Absolute path to write the translation to. Defaults to <name>.<to>.<ext> beside the input.")] string? output,
            [Description("Replace the output file when it already exists. False by default, so nothing is overwritten by accident.")] bool? overwrite) =>
        {
            var full = Path.GetFullPath(path);

            if (!File.Exists(full))
            {
                return McpPayload.Error($"There is no file at {full}. Give a path that exists.");
            }

            if (!DocumentFormats.IsTranslatable(full))
            {
                return McpPayload.Error(DocumentFormats.UnsupportedMessage(full));
            }

            var id = gateway.Jobs.Start("translate_file", 1, async (job, token) =>
                job.Add(await gateway
                    .TranslateFileAsync(full, output, from, to, overwrite ?? false, null, token)
                    .ConfigureAwait(false)));

            return McpPayload.Ok(
                new { job_id = id, file = full, state = "queued" },
                McpPayload.Pairs([("job_id", id), ("file", full), ("state", "queued")]));
        },
        new McpServerToolCreateOptions
        {
            Name = "translate_file",
            Title = "Translate a file",
            Description =
                "Start translating one file on disk and return a job id at once. "
                + "The translation runs in the background: poll job_status with the id until its state is done or failed, "
                + "and read the written path from the job's results. Use translate_text for a string held in memory.",
            ReadOnly = false,
            Destructive = false,
            Idempotent = false,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["job_id", "file", "state"],
                  "properties": {
                    "job_id": { "type": "string", "description": "Pass this to job_status and job_cancel." },
                    "file": { "type": "string", "description": "Absolute path of the file being translated." },
                    "state": { "type": "string", "description": "Always queued: the work has been accepted, not finished." }
                  }
                }
                """),
        });

    private static McpServerTool TranslateBatch(TranslationGateway gateway) => Tool(
        (
            [Description("Absolute path of the folder to translate. Every translatable file directly inside it is queued; subfolders are left alone.")] string folder,
            [Description("Language to translate into, as a code such as cs or de, or its English name.")] string to,
            [Description("Language the files are written in. Defaults to en when omitted.")] string? from,
            [Description("Folder to write the translations to. Defaults to writing beside each input as <name>.<to>.<ext>.")] string? output,
            [Description("Replace output files that already exist. False by default.")] bool? overwrite) =>
        {
            var full = Path.GetFullPath(folder);

            if (!Directory.Exists(full))
            {
                return McpPayload.Error($"There is no folder at {full}. Give a folder that exists.");
            }

            var files = gateway.BatchFiles(full);

            if (files.Count == 0)
            {
                return McpPayload.Error(
                    $"No translatable file in {full}. Accepted: .pdf, .docx, .md, .json, .txt and other plain text.");
            }

            var id = gateway.Jobs.Start("translate_batch", files.Count, (job, token) =>
                gateway.TranslateBatchAsync(
                    full,
                    output,
                    from,
                    to,
                    overwrite ?? false,
                    new Progress<FileTranslation>(job.Add),
                    token));

            return McpPayload.Ok(
                new { job_id = id, folder = full, files = files.Count, state = "queued" },
                McpPayload.Pairs(
                [
                    ("job_id", id),
                    ("folder", full),
                    ("files", files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("state", "queued"),
                ]));
        },
        new McpServerToolCreateOptions
        {
            Name = "translate_batch",
            Title = "Translate a folder",
            Description =
                "Start translating every translatable file in a folder and return a job id at once. "
                + "The work runs in the background one file at a time: poll job_status with the id for per-file results. "
                + "Use translate_file for a single file.",
            ReadOnly = false,
            Destructive = false,
            Idempotent = false,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["job_id", "folder", "files", "state"],
                  "properties": {
                    "job_id": { "type": "string", "description": "Pass this to job_status and job_cancel." },
                    "folder": { "type": "string", "description": "Absolute path of the folder being translated." },
                    "files": { "type": "integer", "description": "How many files were queued." },
                    "state": { "type": "string", "description": "Always queued: the work has been accepted, not finished." }
                  }
                }
                """),
        });

    private static McpServerTool JobStatus(TranslationGateway gateway) => Tool(
        ([Description("The job id returned by translate_file or translate_batch.")] string job_id) =>
        {
            var snapshot = gateway.Jobs.Status(job_id);

            if (snapshot is null)
            {
                return McpPayload.Error($"There is no job called {job_id}. Job ids come back from translate_file and translate_batch.");
            }

            return McpPayload.Ok(
                new
                {
                    id = snapshot.Id,
                    kind = snapshot.Kind,
                    state = snapshot.State.ToString().ToLowerInvariant(),
                    done = snapshot.Done,
                    total = snapshot.Total,
                    finished = snapshot.IsFinished,
                    error = snapshot.Error,
                    results = snapshot.Results.Select(r => new
                    {
                        file = r.File,
                        status = r.Status,
                        @out = r.Out,
                        error = r.Error,
                    }).ToArray(),
                },
                McpPayload.Table(
                    ["file", "status", "out", "error"],
                    snapshot.Results.Count == 0
                        ? [[snapshot.State.ToString().ToLowerInvariant(), $"{snapshot.Done}/{snapshot.Total}", string.Empty, snapshot.Error ?? string.Empty]]
                        : snapshot.Results.Select(r => (IReadOnlyList<string>)[r.File, r.Status, r.Out ?? string.Empty, r.Error ?? string.Empty])));
        },
        new McpServerToolCreateOptions
        {
            Name = "job_status",
            Title = "Job status",
            Description =
                "Read how a translate_file or translate_batch job is going, and its per-file results once it has finished. "
                + "Poll this after starting a job; state is queued, running, done, failed or cancelled.",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["id", "kind", "state", "done", "total", "finished", "results"],
                  "properties": {
                    "id": { "type": "string" },
                    "kind": { "type": "string", "description": "translate_file or translate_batch." },
                    "state": { "type": "string", "description": "queued, running, done, failed or cancelled." },
                    "done": { "type": "integer", "description": "Files finished so far." },
                    "total": { "type": "integer", "description": "Files in the job." },
                    "finished": { "type": "boolean", "description": "True once the job will not change again." },
                    "error": { "type": ["string", "null"], "description": "Why the job failed, when it did." },
                    "results": {
                      "type": "array",
                      "description": "One entry per file, filled in as the job runs.",
                      "items": {
                        "type": "object",
                        "required": ["file", "status"],
                        "properties": {
                          "file": { "type": "string" },
                          "status": { "type": "string", "description": "ok or failed." },
                          "out": { "type": ["string", "null"], "description": "Path the translation was written to." },
                          "error": { "type": ["string", "null"] }
                        }
                      }
                    }
                  }
                }
                """),
        });

    private static McpServerTool JobCancel(TranslationGateway gateway) => Tool(
        ([Description("The job id returned by translate_file or translate_batch.")] string job_id) =>
        {
            var cancelled = gateway.Jobs.Cancel(job_id);

            if (!cancelled)
            {
                return McpPayload.Error($"There is no job called {job_id}, so nothing was cancelled.");
            }

            return McpPayload.Ok(
                new { id = job_id, cancelled = true },
                McpPayload.Pairs([("id", job_id), ("cancelled", "true")]));
        },
        new McpServerToolCreateOptions
        {
            Name = "job_cancel",
            Title = "Cancel a job",
            Description =
                "Stop a translate_file or translate_batch job. Files already written stay written; "
                + "the rest are not translated. Calling it on a finished job changes nothing.",
            ReadOnly = false,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["id", "cancelled"],
                  "properties": {
                    "id": { "type": "string" },
                    "cancelled": { "type": "boolean", "description": "True when the job was found and asked to stop." }
                  }
                }
                """),
        });

    private static McpServerTool ListLanguages(TranslationGateway gateway) => Tool(
        () =>
        {
            var languages = gateway.Languages();

            return McpPayload.Ok(
                new
                {
                    model = gateway.ModelName,
                    languages = languages.Select(l => new
                    {
                        code = l.Code,
                        name = l.Name,
                        endonym = l.Endonym,
                        script = l.Script,
                        direction = l.Direction,
                        availability = l.Availability,
                        reason = l.Reason,
                    }).ToArray(),
                },
                McpPayload.Table(
                    ["code", "name", "endonym", "availability"],
                    languages.Select(l => (IReadOnlyList<string>)[l.Code, l.Name, l.Endonym, l.Availability])));
        },
        new McpServerToolCreateOptions
        {
            Name = "list_languages",
            Title = "List languages",
            Description =
                "List every language this application can be asked for, with the code to pass as 'from' and 'to', "
                + "and whether the selected model supports it. Call this before translating when a code is uncertain.",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["model", "languages"],
                  "properties": {
                    "model": { "type": "string", "description": "Model the availability column was judged against." },
                    "languages": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["code", "name", "endonym", "script", "direction", "availability", "reason"],
                        "properties": {
                          "code": { "type": "string", "description": "Pass this as 'from' or 'to'." },
                          "name": { "type": "string", "description": "English name." },
                          "endonym": { "type": "string", "description": "The language's own name." },
                          "script": { "type": "string", "description": "ISO 15924 script code." },
                          "direction": { "type": "string", "description": "ltr or rtl." },
                          "availability": { "type": "string", "description": "supported, unverified or unsupported for the selected model." },
                          "reason": { "type": "string", "description": "Why the availability reads as it does." }
                        }
                      }
                    }
                  }
                }
                """),
        });

    private static McpServerTool ListModels(TranslationGateway gateway) => Tool(
        () =>
        {
            var models = gateway.Models();

            return McpPayload.Ok(
                new
                {
                    models = models.Select(m => new
                    {
                        name = m.Name,
                        file = m.FileName,
                        path = m.Path,
                        size_bytes = m.SizeBytes,
                        installed = m.Installed,
                        selected = m.Selected,
                    }).ToArray(),
                },
                McpPayload.Table(
                    ["name", "file", "installed", "selected"],
                    models.Select(m => (IReadOnlyList<string>)
                        [m.Name, m.FileName, m.Installed ? "yes" : "no", m.Selected ? "yes" : "no"])));
        },
        new McpServerToolCreateOptions
        {
            Name = "list_models",
            Title = "List models",
            Description =
                "List the translation models on this machine and which one translates right now. "
                + "Use the name from here with select_model.",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["models"],
                  "properties": {
                    "models": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["name", "file", "path", "size_bytes", "installed", "selected"],
                        "properties": {
                          "name": { "type": "string", "description": "Pass this to select_model." },
                          "file": { "type": "string", "description": "File name of the model." },
                          "path": { "type": "string", "description": "Where it sits, empty when it is not installed." },
                          "size_bytes": { "type": "integer" },
                          "installed": { "type": "boolean" },
                          "selected": { "type": "boolean", "description": "True for the model that translates now." }
                        }
                      }
                    }
                  }
                }
                """),
        });

    private static McpServerTool SelectModel(TranslationGateway gateway, IGuiBridge gui) => Tool(
        async (
            [Description("Name, file name or full path of an installed model, as list_models reports it.")] string name,
            CancellationToken cancellationToken) =>
        {
            var (model, error) = await gateway.SelectModelAsync(name, cancellationToken).ConfigureAwait(false);

            if (model is null)
            {
                return McpPayload.Error(error!.Message);
            }

            await gui.ModelChangedAsync(cancellationToken).ConfigureAwait(false);

            return McpPayload.Ok(
                new { name = model.Name, file = model.FileName, path = model.Path, selected = true },
                McpPayload.Pairs([("name", model.Name), ("file", model.FileName), ("path", model.Path)]));
        },
        new McpServerToolCreateOptions
        {
            Name = "select_model",
            Title = "Select the model",
            Description =
                "Choose which installed model translates from now on. The choice is stored and outlives this server. "
                + "An application window picks it up at once when this server is the one running inside it, and on its "
                + "next settings read otherwise. Call list_models first for the names that are installed.",
            ReadOnly = false,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["name", "file", "path", "selected"],
                  "properties": {
                    "name": { "type": "string" },
                    "file": { "type": "string" },
                    "path": { "type": "string" },
                    "selected": { "type": "boolean", "description": "Always true: the model is now the selected one." }
                  }
                }
                """),
        });

    private static McpServerTool ShowInGui(IGuiBridge gui) => Tool(
        async (
            [Description("Entry id to reveal, as get_entry reports it. Omit to bring the window forward without selecting anything.")] string? entry_id,
            CancellationToken cancellationToken) =>
        {
            Guid? id = null;

            if (!string.IsNullOrWhiteSpace(entry_id))
            {
                if (!Guid.TryParse(entry_id, out var parsed))
                {
                    return McpPayload.Error($"'{entry_id}' is not an entry id. Ids are GUIDs, as get_entry reports them.");
                }

                id = parsed;
            }

            var outcome = await gui.ShowAsync(id, cancellationToken).ConfigureAwait(false);

            if (!outcome.Shown)
            {
                return McpPayload.Error(outcome.Detail);
            }

            return McpPayload.Ok(
                new { shown = true, detail = outcome.Detail },
                McpPayload.Pairs([("shown", "true"), ("detail", outcome.Detail)]));
        },
        new McpServerToolCreateOptions
        {
            Name = "show_in_gui",
            Title = "Show in the window",
            Description =
                "Bring the BetterTranslator window forward, and select one stored entry in it when an id is given. "
                + "Use after get_entry to put a translation in front of the person at the machine.",
            ReadOnly = false,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["shown", "detail"],
                  "properties": {
                    "shown": { "type": "boolean" },
                    "detail": { "type": "string", "description": "What the window was asked to do." }
                  }
                }
                """),
        });

    private static McpServerTool GetEntry(TranslationGateway gateway) => Tool(
        async (
            [Description("The entry id, a GUID. Entries are the stored translations the application window shows.")] string entry_id,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(entry_id, out var id))
            {
                return McpPayload.Error($"'{entry_id}' is not an entry id. Ids are GUIDs.");
            }

            var entry = await gateway.GetEntryAsync(id, cancellationToken).ConfigureAwait(false);

            if (entry is null)
            {
                return McpPayload.Error($"There is no stored entry with id {id}.");
            }

            return McpPayload.Ok(
                new
                {
                    id = entry.Id,
                    chat_id = entry.ChatId,
                    kind = entry.Kind,
                    state = entry.State,
                    source = McpPayload.Clip(entry.Source, McpPayload.FieldLimit),
                    result = McpPayload.Clip(entry.Result, McpPayload.FieldLimit),
                    target_language = entry.TargetLanguage,
                    created_at = entry.CreatedAt,
                    file_name = entry.FileName,
                    generated_tokens = entry.GeneratedTokens,
                    duration_ms = entry.DurationMs,
                },
                McpPayload.Pairs(
                [
                    ("id", entry.Id),
                    ("kind", entry.Kind),
                    ("state", entry.State),
                    ("target_language", entry.TargetLanguage),
                    ("source", entry.Source),
                    ("result", entry.Result),
                ]));
        },
        new McpServerToolCreateOptions
        {
            Name = "get_entry",
            Title = "Get a stored entry",
            Description =
                "Read one stored translation by its id: what was sent, what came back, the target language and what it cost. "
                + "Use when an entry id is already known; there is no listing tool.",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            OutputSchema = McpPayload.Schema(
                """
                {
                  "type": "object",
                  "required": ["id", "chat_id", "kind", "state", "source", "result", "target_language", "created_at"],
                  "properties": {
                    "id": { "type": "string" },
                    "chat_id": { "type": "string" },
                    "kind": { "type": "string", "description": "Words, Sentence or File." },
                    "state": { "type": "string", "description": "Pending, Done or Failed." },
                    "source": { "type": "string", "description": "The text that was sent. A long body is clipped and says so at its end." },
                    "result": { "type": "string", "description": "The translation, empty when there is none. A long body is clipped and says so at its end." },
                    "target_language": { "type": "string" },
                    "created_at": { "type": "string", "description": "ISO 8601 timestamp." },
                    "file_name": { "type": ["string", "null"] },
                    "generated_tokens": { "type": ["integer", "null"] },
                    "duration_ms": { "type": ["integer", "null"] }
                  }
                }
                """),
        });
}
