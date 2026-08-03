namespace MediaDownloader.Services.Downloads;

/// <summary>Tunable knobs for the download engine, bound from the "DownloadEngine" config section.</summary>
public class DownloadEngineOptions
{
    /// <summary>
    /// Cache root for DHT state, magnet metadata, fast-resume data, and private-tracker torrent
    /// files. Null uses the application's per-user data directory.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// How long a download may sit fetching magnet metadata before giving up and marking it
    /// failed, in minutes. A dead torrent (no seeders) otherwise shows "Fetching metadata"
    /// forever with no feedback.
    /// </summary>
    public int MetadataTimeoutMinutes { get; set; } = 3;
}
