namespace BetterTranslator.Cli;

public static class Help
{
    public const string ExitCodes =
        """
        EXIT CODES
          0  success
          2  usage or argument error
          3  input file missing or unreadable
          4  model runtime unreachable
          5  translation failed or incomplete
        """;

    public static string For(Verb verb) => verb switch
    {
        Verb.Translate => Translate,
        Verb.Languages => Languages,
        Verb.Models => Models,
        Verb.Mcp => Mcp,
        _ => Root,
    };

    public const string Mcp =
        $"""
        bt mcp - serve the agent tools over stdin and stdout.

        USAGE
          bt mcp

        For an agent that speaks MCP over a pipe rather than over HTTP, and for
        machines with no domain and nothing published: the agent starts this
        process and talks to it directly. No port is opened and nothing listens.

        The same ten tools the in-app server exposes are served here, except that
        show_in_gui reports that there is no window when the application is not
        running.

        stdout carries the protocol and nothing else in this mode, so --json is
        refused. Diagnostics go to stderr.

        REGISTER IT
          Give the full path to bt.exe. A bare bt only works when it is on PATH,
          and an agent that cannot find it reports "connection closed" with
          nothing else to go on.

          Claude Code, for every project on this machine:
            claude mcp add -s user bettertranslator -- "C:\path\to\bt.exe" mcp

          Drop -s user to register it for the current project only. Settings >
          Agent in the application shows the command with the path filled in.

          A client with a JSON config file, under its mcpServers object:
            "bettertranslator": with command set to the full path of bt.exe and
            args set to a single element, mcp

        WHEN IT WILL NOT CONNECT
          The first line on stderr names the version, the build time and the
          path of the executable that answered. A client reports every one of
          these as "connection closed", so read that line first: a build time
          older than the last change to this project means the agent is running
          a stale copy that has no mcp command. Rebuild, then reconnect.

        {ExitCodes}
        """;

    public const string Root =
        $"""
        bt - BetterTranslator on the command line. Local models, no network.

        USAGE
          bt <command> [options]

        COMMANDS
          translate    translate text, one file, or a folder of files
          languages    list the languages the selected model can translate
          models       list installed models, or select one
          mcp          serve the agent tools over stdin and stdout

        GLOBAL OPTIONS
          --from <lang>     source language code or name (default: en)
          --to <lang>       target language code or name (required for translate)
          --json            print one JSON envelope on stdout and nothing else
          --quiet           no progress on stderr
          --memory          translate against the indexed project and its
                            glossary, as the window does with its Memory chip
          --verify          add the verification the window computes to the
                            JSON envelope: completion, red spans, skipped checks
          --help            this text, or a command's own
          --version         print the version and exit

        OUTPUT
          stdout carries the result or the JSON envelope. Everything else goes to
          stderr. Nothing ever prompts.

        EXAMPLES
          bt translate "The build is green." --from en --to cs
          echo Hello | bt translate --from en --to cs
          bt translate --file README.md --to cs
          bt translate --batch .\docs --to cs --out .\docs-cs
          bt languages --json
          bt models --select EuroLLM

        {ExitCodes}
        """;

    public const string Translate =
        $"""
        bt translate - translate text, one file, or a folder of files.

        USAGE
          bt translate <text> --to <lang> [--from <lang>]
          bt translate --to <lang> [--from <lang>]              reads stdin
          bt translate --file <in> --to <lang> [--out <path>]
          bt translate --batch <dir> --to <lang> [--out <dir>]

        OPTIONS
          --from <lang>   source language code or name. Default: en
          --to <lang>     target language code or name. Required
          --file <path>   translate one file
          --batch <dir>   translate every translatable file in a folder
          --out <path>    output file for --file, output folder for --batch.
                          Default: <name>.<to>.<ext> beside the input
          --overwrite     replace an output file that already exists
          --json          one JSON envelope on stdout
          --quiet         no progress on stderr

        FORMATS
          .json keeps its keys, .md keeps its structure, other text keeps its
          line count. .pdf and .docx are read the way the application window
          reads them and written back as text, named <name>.<ext>.<to>.txt,
          because neither can be written back. Formats with no reader -- .doc,
          .odt, .rtf, spreadsheets, archives -- are refused.

        EXAMPLES
          bt translate "Hello" --from en --to cs
          bt translate --file notes.md --to cs --out notes.cs.md

        {ExitCodes}
        """;

    public const string Languages =
        $"""
        bt languages - list the languages the selected model can translate.

        USAGE
          bt languages [--json]

        OPTIONS
          --json    an ok envelope carrying a languages array, on stdout

        EXAMPLES
          bt languages
          bt languages --json

        {ExitCodes}
        """;

    public const string Models =
        $"""
        bt models - list installed models, or select the one that translates.

        USAGE
          bt models [--json]
          bt models --select <name> [--json]

        OPTIONS
          --select <name>   make this model the one used from now on
          --json            an ok envelope carrying a models array, on stdout

        EXAMPLES
          bt models
          bt models --select EuroLLM

        {ExitCodes}
        """;
}
