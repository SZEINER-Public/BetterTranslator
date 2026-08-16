using BetterTranslator.Runtime.Agents;

namespace BetterTranslator.Cli;

public static class ExitCode
{
    public const int Success = 0;
    public const int Usage = 2;
    public const int InputMissing = 3;
    public const int RuntimeUnreachable = 4;
    public const int TranslationFailed = 5;

    public static int For(AgentFault fault) => fault switch
    {
        AgentFault.None => Success,
        AgentFault.InputMissing => InputMissing,
        AgentFault.ModelMissing => RuntimeUnreachable,
        AgentFault.RuntimeUnreachable => RuntimeUnreachable,
        AgentFault.TranslationFailed => TranslationFailed,
        _ => Usage,
    };

    public static int For(string? code) => code switch
    {
        null or "ok" => Success,
        "input_missing" => InputMissing,
        "model_missing" or "runtime_unreachable" => RuntimeUnreachable,
        "translation_failed" => TranslationFailed,
        _ => Usage,
    };
}
