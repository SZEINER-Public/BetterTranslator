using Avalonia.Threading;

namespace BetterTranslator.Mac.Parity;

public static class Pump
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public static void Wait(Task task)
    {
        var started = DateTime.UtcNow;

        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);

            if (DateTime.UtcNow - started > Limit)
            {
                throw new TimeoutException("A seeded load did not complete within " + Limit + ".");
            }
        }

        task.GetAwaiter().GetResult();
    }

    public static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.Result;
    }
}
