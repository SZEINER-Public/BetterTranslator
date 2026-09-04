namespace BetterTranslator.Core.Verification.Checks;

public interface ICheck
{
    string CheckId { get; }

    string Category { get; }

    IReadOnlyList<CheckFinding> Run(CheckContext context);
}
