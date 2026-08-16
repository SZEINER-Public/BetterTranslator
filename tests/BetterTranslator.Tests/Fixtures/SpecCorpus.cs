namespace BetterTranslator.Tests;

/// <summary>
/// The messages this pipeline is actually asked to translate, as fixtures.
///
/// Every family is real text a reader pasted into the app, not a sample written
/// to pass. Family A carries the reported defect verbatim: a sentence of
/// ordinary prose that came back still in English because the markup around it
/// stopped it from ever becoming a unit.
/// </summary>
public static class SpecCorpus
{
    /// <summary>
    /// The reported defect, on its own. It is prose. It has to end up in Czech,
    /// with the placeholder and both flags intact.
    /// </summary>
    public const string ReportedDefect =
        "Default output naming: <name>.<to>.<ext> beside the input unless --out is given. "
        + "Batch emits one status line per file, or one JSON result entry per file in --json mode.";

    /// <summary>An agent-facing CLI specification: placeholders, flags, an exit-code table, JSON literals.</summary>
    public const string CommandSurface =
        """
        Implement this command surface:

        bt translate <text>
        bt translate            (reads stdin when no text argument is given)
        bt translate --file <in> [--out <path>]
        bt translate --batch <dir> [--out <dir>]
        bt languages
        bt models [--select <name>]
        bt --version, bt --help, bt <command> --help

        Shared options: --from <lang>, --to <lang>, --json, --quiet, --context-dir <path>.

        Default output naming: <name>.<to>.<ext> beside the input unless --out is given. Batch emits one status line per file, or one JSON result entry per file in --json mode.

        stdout carries only the translation result or the JSON envelope. All logs, progress, and warnings go to stderr. Never prompt for input, and show no spinner when stdout is redirected. UTF-8 in and out, with no BOM on stdout.

        Exit codes: 0 success, 2 usage or argument error, 3 input file missing or unreadable, 4 model runtime unreachable, 5 translation failed or incomplete. Document the table in --help.

        The text envelope is {"ok":true,"from":"","to":"","model":"","result":""} and the failure envelope is {"ok":false,"error":{"code":0,"message":""}}. Never mix human-readable text into stdout in --json mode.
        """;

    /// <summary>
    /// Policy blocks. The ALL-CAPS label before the colon is a label and stays;
    /// the prose after it is prose and gets translated. OUTPUT and SCOPE are
    /// also ordinary English words, which is the point of including them.
    /// </summary>
    public const string PolicyBlocks =
        """
        FEATURE: JSON-aware mode for the same message textbox. The primary use case is i18n resource files, which map string keys to translatable values.

        COMMENTS: none. Write no comments in any generated file, and express intent through naming and structure instead. Keep only machine-read directives where the toolchain requires them.

        GIT: Do not create commits and do not publish. Read-only inspection is allowed, and every change is left in the working tree for the user to review.

        ATTRIBUTION: Never name yourself or any system in an artifact. The user is the sole author of record.

        OUTPUT: Code and file edits only. No preamble, no plan narration, and no summary of what changed.

        SCOPE: Deliver what was asked, at the scope intended. Finish the whole task, and stop short of anything clearly beyond it.
        """;

    /// <summary>Markdown structure that must survive the round trip byte for byte.</summary>
    public static readonly string MixedMarkdown =
        """
        ## Build and publish

        The app builds to a launchable Windows executable, and never to a class library.

        | Setting | Value |
        |---|---|
        | Output type | WinExe |
        | Target framework | net10.0-windows |

        Run the distributable build from the repository root:

        ```bash
        dotnet publish src/BetterTranslator.App -c Release -r win-x64 --self-contained true
        ```

        - [x] The solution builds with zero warnings
        - [ ] The publish profile produces a single file

        1. Read the checklist above before shipping.
        2. Fix **every** failing item, then run it again.
        3. Confirm that `dotnet test` still passes.

        """
        // Two trailing spaces are a hard line break in CommonMark and an editor
        // that trims trailing whitespace would silently delete this fixture's
        // subject, so the break is spelled out rather than typed.
        + "This line ends in a hard break," + "  " + "\nand this is its continuation.\n";

    /// <summary>An i18n resource object: placeholders, an escaped quote, a URL, an empty value.</summary>
    public const string ResourceJson =
        """
        {
          "app": {
            "title": "BetterTranslator",
            "greeting": "Welcome back, {name}",
            "itemCount": "You have {count} unread messages",
            "quote": "The setting is called \"Shared options\" in the panel",
            "docs": "https://example.com/docs/getting-started",
            "save": "Save",
            "empty": ""
          },
          "errors": {
            "runtimeUnreachable": "The model runtime is unreachable. Start it and try again."
          }
        }
        """;

    /// <summary>
    /// The do-not-translate direction. Every sentence here has to end up in
    /// Czech; every name in it has to survive byte-identical.
    /// </summary>
    public const string ComponentNames =
        """
        Survey Core, Engine, Runtime, and Indexing to locate the service entry points the interface uses for text translation, file translation, batch translation, language listing, and model selection.

        Add src/BetterTranslator.Cli, a net10.0 console project referencing the shared projects. It runs headless, and its only external dependency is the LM Studio runtime endpoint.

        System.CommandLine is the only new package permitted, and everything else comes from the base class library. Chat rows are stored in SQLite, Markdig parses the Markdown, and the tests live beside the existing projects in tests/BetterTranslator.Tests.

        Do not change the MCP tool surface, and never reference the App project from the command line project.
        """;

    /// <summary>Names, flags, placeholders and paths that must appear in the output exactly as they appear in the source.</summary>
    public static readonly IReadOnlyList<string> MustSurviveVerbatim =
    [
        "Core", "Engine", "Runtime", "Indexing", "App",
        "BetterTranslator", "BetterTranslator.Cli", "System.CommandLine", "LM Studio",
        "MCP", "SQLite", "Markdig", "WinExe", "net10.0-windows",
        "src/BetterTranslator.Cli", "tests/BetterTranslator.Tests",
        "stdout", "stderr", "UTF-8", "BOM",
        "--from", "--to", "--json", "--quiet", "--out", "--file", "--batch", "--context-dir",
        "--version", "--help", "--select", "--self-contained",
        "<name>", "<to>", "<ext>", "<in>", "<dir>", "<path>", "<text>", "<lang>", "<command>",
        "{name}", "{count}",
        "https://example.com/docs/getting-started",
    ];

    public static IReadOnlyList<(string Name, string Text)> All =>
    [
        (nameof(CommandSurface), CommandSurface),
        (nameof(PolicyBlocks), PolicyBlocks),
        (nameof(MixedMarkdown), MixedMarkdown),
        (nameof(ResourceJson), ResourceJson),
        (nameof(ComponentNames), ComponentNames),
    ];
}
