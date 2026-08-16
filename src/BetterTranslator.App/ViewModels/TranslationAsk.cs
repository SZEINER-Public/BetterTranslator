namespace BetterTranslator.App.ViewModels;

/// <summary>
/// What the composer hands the shell for one send.
///
/// <paramref name="Memory"/> is null on every send where the Memory chip is not
/// attached, which is the normal one. It is the only route by which anything
/// other than this text reaches the model, so a null here is the guarantee that
/// the context is free.
/// </summary>
/// <param name="UsesMemory">
/// The chip itself, which is not the same question as whether it produced
/// anything. Retrieval can come back empty on a chat with nothing indexed yet,
/// and the project's own glossary still applies in that case -- the chip is the
/// reader saying "bring what this project knows", and an empty answer to that is
/// still an answer.
/// </param>
public sealed record TranslationAsk(
    string Text,
    Core.Languages.TranslationDirection Direction,
    string? Memory,
    bool UsesMemory = false);
