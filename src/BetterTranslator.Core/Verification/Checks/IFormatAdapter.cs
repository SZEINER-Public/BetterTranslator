namespace BetterTranslator.Core.Verification.Checks;

public interface IFormatAdapter
{
    string Format { get; }

    DocumentModel Read(string text);
}
