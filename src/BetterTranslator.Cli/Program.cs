using System.Text;
using BetterTranslator.Cli;
using BetterTranslator.Runtime.Agents;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

Console.OutputEncoding = utf8;
Console.InputEncoding = utf8;

var command = CommandLine.Parse(args);

if (!command.IsValid)
{
    return Fail(ExitCode.Usage, command.Error!, command.Json);
}

if (command.WantsHelp)
{
    Console.Out.WriteLine(Help.For(command.Verb));
    return ExitCode.Success;
}

if (command.Verb == Verb.Version)
{
    Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    return ExitCode.Success;
}

if (command.Verb == Verb.Mcp)
{
    Console.Error.WriteLine(StdioServer.Banner());
}

try
{
    using var gateway = await TranslationGateway.StartAsync(CancellationToken.None);

    return command.Verb switch
    {
        Verb.Translate => await TranslateAsync(gateway, command),
        Verb.Languages => Languages(gateway, command),
        Verb.Models => await ModelsAsync(gateway, command),
        Verb.Mcp => await StdioServer.RunAsync(gateway, CancellationToken.None),
        _ => Fail(ExitCode.Usage, "Nothing to do. Run bt --help.", command.Json),
    };
}
catch (Exception ex)
{
    return Fail(ExitCode.RuntimeUnreachable, ex.Message, command.Json);
}

async Task<int> TranslateAsync(TranslationGateway gateway, ParsedCommand parsed)
{
    if (parsed.Batch is not null)
    {
        Note(parsed, $"translating {parsed.Batch}");

        var results = await gateway.TranslateBatchAsync(
            parsed.Batch,
            parsed.Out,
            parsed.From,
            parsed.To,
            parsed.Overwrite,
            new Progress<FileTranslation>(step =>
                Note(parsed, $"{step.Status,-6} {Path.GetFileName(step.File)}")),
            CancellationToken.None);

        if (results.Count == 0)
        {
            return Fail(ExitCode.InputMissing, $"No translatable file in {parsed.Batch}.", parsed.Json);
        }

        if (parsed.Json)
        {
            Console.Out.WriteLine(Envelope.Files(results));
        }
        else
        {
            foreach (var result in results)
            {
                Console.Out.WriteLine(result.Status == "ok"
                    ? $"ok      {result.File} -> {result.Out}"
                    : $"failed  {result.File}  {result.Error}");
            }
        }

        var worst = results.FirstOrDefault(r => r.Status != "ok");

        return worst is null ? ExitCode.Success : ExitCode.For(worst.ErrorCode);
    }

    if (parsed.File is not null)
    {
        Note(parsed, $"translating {parsed.File}");

        var result = await gateway.TranslateFileAsync(
            parsed.File,
            parsed.Out,
            parsed.From,
            parsed.To,
            parsed.Overwrite,
            null,
            CancellationToken.None);

        if (result.Status != "ok")
        {
            return Fail(ExitCode.For(result.ErrorCode), result.Error ?? "The file was not translated.", parsed.Json);
        }

        if (parsed.Json)
        {
            Console.Out.WriteLine(Envelope.Files([result]));
        }
        else
        {
            Console.Out.WriteLine(result.Out);
        }

        return ExitCode.Success;
    }

    var text = parsed.Text ?? await ReadStdinAsync();

    if (string.IsNullOrWhiteSpace(text))
    {
        return Fail(ExitCode.Usage, "There is no text to translate. Pass it as an argument or on stdin.", parsed.Json);
    }

    var translation = await gateway.TranslateTextAsync(text, parsed.From, parsed.To, parsed.Memory, CancellationToken.None);

    if (!translation.Ok)
    {
        return Fail(ExitCode.For(translation.Error!.Fault), translation.Error.Message, parsed.Json);
    }

    if (parsed.Json)
    {
        Console.Out.WriteLine(Envelope.Text(translation));
    }
    else
    {
        Console.Out.WriteLine(translation.Text);

        // stdout carries the translation and nothing else, so what the run did
        // to the text goes where the progress went.
        if (translation.Note is not null)
        {
            Note(parsed, translation.Note);
        }
    }

    return ExitCode.Success;
}

int Languages(TranslationGateway gateway, ParsedCommand parsed)
{
    var rows = gateway.Languages()
        .Select(l => new LanguageRow
        {
            Code = l.Code,
            Name = l.Name,
            Endonym = l.Endonym,
            Availability = l.Availability,
        })
        .ToList();

    if (parsed.Json)
    {
        Console.Out.WriteLine(Envelope.Languages(rows));
        return ExitCode.Success;
    }

    foreach (var row in rows)
    {
        Console.Out.WriteLine($"{row.Code,-6} {row.Name,-24} {row.Availability}");
    }

    return ExitCode.Success;
}

async Task<int> ModelsAsync(TranslationGateway gateway, ParsedCommand parsed)
{
    if (parsed.Select is not null)
    {
        var (model, error) = await gateway.SelectModelAsync(parsed.Select, CancellationToken.None);

        if (model is null)
        {
            return Fail(ExitCode.For(error!.Fault), error.Message, parsed.Json);
        }

        Note(parsed, $"selected {model.Name}");
    }

    var rows = gateway.Models()
        .Select(m => new ModelRow
        {
            Name = m.Name,
            File = m.FileName,
            Path = m.Path,
            Installed = m.Installed,
            Selected = m.Selected,
        })
        .ToList();

    if (parsed.Json)
    {
        Console.Out.WriteLine(Envelope.Models(rows));
        return ExitCode.Success;
    }

    foreach (var row in rows)
    {
        var mark = row.Selected ? "*" : row.Installed ? " " : "-";
        Console.Out.WriteLine($"{mark} {row.Name,-18} {row.File}");
    }

    return ExitCode.Success;
}

static async Task<string> ReadStdinAsync()
{
    if (Console.IsInputRedirected)
    {
        return await Console.In.ReadToEndAsync();
    }

    return string.Empty;
}

static void Note(ParsedCommand parsed, string message)
{
    if (!parsed.Quiet && !parsed.Json)
    {
        Console.Error.WriteLine(message);
    }
}

static int Fail(int code, string message, bool json)
{
    if (json)
    {
        Console.Out.WriteLine(Envelope.Failure(code, message));
    }
    else
    {
        Console.Error.WriteLine(message);
    }

    return code;
}

public partial class Program;
