namespace BetterTranslator.Core.Verification.Checks;

public interface ISkippableCheck
{
    string? SkipReason(CheckContext context);
}
