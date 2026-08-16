namespace BetterTranslator.Cli;

public enum Verb
{
    None,
    Translate,
    Languages,
    Models,
    Mcp,
    Help,
    Version,
}

public sealed record ParsedCommand
{
    public Verb Verb { get; init; } = Verb.None;

    public string? Text { get; init; }

    public string? File { get; init; }

    public string? Batch { get; init; }

    public string? Out { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Select { get; init; }

    /// <summary>
    /// Translate against the indexed project and its glossary, as the window
    /// does with its Memory chip attached. Off unless asked for.
    /// </summary>
    public bool Memory { get; init; }

    public bool Json { get; init; }

    public bool Quiet { get; init; }

    public bool Overwrite { get; init; }

    public bool WantsHelp { get; init; }

    public string? Error { get; init; }

    public bool IsValid => Error is null;
}

public static class CommandLine
{
    private static readonly string[] Verbs = ["translate", "languages", "models", "mcp"];

    private static readonly string[] Valued =
        ["--from", "--to", "--file", "--batch", "--out", "--select"];

    /// <summary>
    /// Options this command line knows the name of and cannot honour. Listed
    /// here rather than parsed and refused later: an option that only ever ends
    /// in an error is not an option, and advertising it in the help implied a
    /// capability that was never there. Naming it here keeps the answer specific
    /// -- what it would have done, and where that lives instead -- without
    /// pretending it is accepted.
    /// </summary>
    private static readonly (string Name, string Reason)[] Unavailable =
    [
        ("--context-dir",
            "index scope is chosen in the application, and this command line cannot narrow it. "
            + "Nothing bt translates carries indexed context."),
    ];

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new ParsedCommand { Verb = Verb.Help, WantsHelp = true };
        }

        var json = args.Contains("--json", StringComparer.Ordinal);
        var first = args[0];

        if (first is "--help" or "-h" or "-?" or "help")
        {
            return new ParsedCommand { Verb = Verb.Help, WantsHelp = true };
        }

        if (first is "--version" or "-v")
        {
            return new ParsedCommand { Verb = Verb.Version };
        }

        if (!Verbs.Contains(first, StringComparer.Ordinal))
        {
            return new ParsedCommand
            {
                Json = json,
                Error = $"'{first}' is not a command. Commands: {string.Join(", ", Verbs)}. Run bt --help.",
            };
        }

        var verb = first switch
        {
            "translate" => Verb.Translate,
            "languages" => Verb.Languages,
            "mcp" => Verb.Mcp,
            _ => Verb.Models,
        };

        var command = new ParsedCommand { Verb = verb, Json = json };
        var free = new List<string>();

        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];

            switch (token)
            {
                case "--help" or "-h" or "-?":
                    return command with { WantsHelp = true };

                case "--json":
                    command = command with { Json = true };
                    continue;

                case "--quiet" or "-q":
                    command = command with { Quiet = true };
                    continue;

                case "--overwrite":
                    command = command with { Overwrite = true };
                    continue;

                case "--memory":
                    command = command with { Memory = true };
                    continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var name = token;
                string? value = null;

                var equals = token.IndexOf('=', StringComparison.Ordinal);

                if (equals > 0)
                {
                    name = token[..equals];
                    value = token[(equals + 1)..];
                }

                // Before the value is taken, so a name nobody can honour is
                // answered by name whichever way it was written.
                var unavailable = Array.Find(Unavailable, o => string.Equals(o.Name, name, StringComparison.Ordinal));

                if (unavailable.Name is not null)
                {
                    return command with { Error = $"{unavailable.Name} is not available: {unavailable.Reason}" };
                }

                if (equals <= 0)
                {
                    if (!Valued.Contains(name, StringComparer.Ordinal))
                    {
                        return command with { Error = $"{name} is not an option. Run bt {first} --help." };
                    }

                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return command with { Error = $"{token} needs a value." };
                    }

                    value = args[++i];
                }

                command = name switch
                {
                    "--from" => command with { From = value },
                    "--to" => command with { To = value },
                    "--file" => command with { File = value },
                    "--batch" => command with { Batch = value },
                    "--out" => command with { Out = value },
                    "--select" => command with { Select = value },
                    _ => command with { Error = $"{name} is not an option. Run bt {first} --help." },
                };

                if (!command.IsValid)
                {
                    return command;
                }

                continue;
            }

            free.Add(token);
        }

        if (free.Count > 0)
        {
            if (verb != Verb.Translate)
            {
                return command with { Error = $"bt {first} takes no free arguments. Run bt {first} --help." };
            }

            command = command with { Text = string.Join(' ', free) };
        }

        return Validate(command);
    }

    private static ParsedCommand Validate(ParsedCommand command)
    {
        if (command.Verb == Verb.Translate)
        {
            if (command.File is not null && command.Batch is not null)
            {
                return command with { Error = "Pass --file or --batch, not both." };
            }

            if (command.Text is not null && (command.File is not null || command.Batch is not null))
            {
                return command with { Error = "Pass text, --file or --batch, not more than one of them." };
            }

            if (command.To is null)
            {
                return command with { Error = "--to is required. Run bt languages for the codes." };
            }
        }

        if (command.Verb == Verb.Languages && (command.File is not null || command.Batch is not null))
        {
            return command with { Error = "bt languages takes no --file or --batch." };
        }

        if (command.Verb == Verb.Mcp && command.Json)
        {
            return command with
            {
                Error = "bt mcp speaks the agent protocol on stdout, so --json cannot be combined with it.",
            };
        }

        return command;
    }
}
