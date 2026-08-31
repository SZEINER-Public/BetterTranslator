namespace BetterTranslator.Engine.Documents;

public static class Stoppable
{
    public static async Task<(string? Answer, bool Stopped)> UnitAsync(
        Func<string, CancellationToken, Task<string?>> translate,
        string source,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }

        try
        {
            return (await translate(source, cancellationToken).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            return (null, true);
        }
    }
}
