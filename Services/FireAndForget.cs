namespace MediaDownloader.Services;

/// <summary>
/// Runs a fire-and-forget async operation, catching and logging any exception instead of letting it
/// surface only as a delayed, GC-timing-dependent UnobservedTaskException. Awaiting the returned
/// Task is optional — it never faults, so `_ = FireAndForget.RunSafe(...)` is safe.
/// </summary>
public static class FireAndForget
{
    public static async Task RunSafe(Func<Task> operation, ILogger logger, string errorMessage, params object?[] args)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, errorMessage, args);
        }
    }
}
