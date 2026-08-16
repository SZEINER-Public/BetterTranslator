namespace BetterTranslator.SingleFileProbe;

internal static class Program
{
    private const string ExitPrefix = "--exit=";

    private static int Main(string[] args)
    {
        Console.Out.WriteLine($"argc={args.Length}");

        foreach (string argument in args)
        {
            Console.Out.WriteLine($"arg=[{argument}]");
        }

        Console.Out.WriteLine($"raw=[{Environment.CommandLine}]");
        Console.Out.WriteLine($"base=[{AppContext.BaseDirectory}]");
        Console.Out.WriteLine($"process=[{Environment.ProcessPath}]");
        Console.Out.Flush();

        foreach (string argument in args)
        {
            if (argument.StartsWith(ExitPrefix, StringComparison.Ordinal) &&
                int.TryParse(argument[ExitPrefix.Length..], out int code))
            {
                return code;
            }
        }

        return 0;
    }
}
