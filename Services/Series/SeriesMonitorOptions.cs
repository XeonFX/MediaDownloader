namespace MediaDownloader.Services.Series;

/// <summary>Tunable knobs for the series-episode scheduler, bound from the "SeriesMonitor" config section.</summary>
public class SeriesMonitorOptions
{
    /// <summary>How often to check whether any series task is due for a check, in minutes.</summary>
    public int PollIntervalMinutes { get; set; } = 1;

    /// <summary>
    /// Max number of episodes to queue in a single check pass, to bound worst-case work per tick
    /// if a loosely-configured task matches many episodes at once.
    /// </summary>
    public int MaxEpisodesPerCheck { get; set; } = 25;
}
