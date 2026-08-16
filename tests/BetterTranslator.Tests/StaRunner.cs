using System.Windows;

namespace BetterTranslator.Tests;

/// <summary>
/// WPF resources are DispatcherObjects owned by the thread that made them, so
/// anything that touches a loaded ResourceDictionary has to run on one STA
/// thread and hand back a plain result. Assert inside the callback, not after.
/// </summary>
internal static class StaRunner
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // Constructing an Application registers the pack:// scheme that
                // resource URIs below rely on.
                if (Application.Current is null)
                {
                    _ = new Application();
                }

                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("Work on the STA thread failed: " + failure.Message, failure);
        }

        return result;
    }
}
