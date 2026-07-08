namespace MediaDownloader.Data.Entities;

public class SeriesTask
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Search query sent to the torrent providers, e.g. "One Piece 1080p".</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Provider name to search, or null/empty for all providers.</summary>
    public string? Provider { get; set; }

    /// <summary>Optional extra text that must appear in the result title (e.g. "1080p" or a release group).</summary>
    public string? TitleFilter { get; set; }

    /// <summary>Optional season number. When set, only S{Season}E{Episode} style results match.</summary>
    public int? Season { get; set; }

    public int StartEpisode { get; set; } = 1;
    public int? EndEpisode { get; set; }

    /// <summary>Optional download folder for this series. When empty, the global folder from Settings is used.</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>Highest episode number already downloaded (0 = nothing yet).</summary>
    public int LastDownloadedEpisode { get; set; }

    public int CheckIntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    public DateTime? LastCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<DownloadItem> Downloads { get; set; } = new();

    public int NextEpisode => Math.Max(StartEpisode, LastDownloadedEpisode + 1);
    public bool IsFinished => EndEpisode.HasValue && LastDownloadedEpisode >= EndEpisode.Value;
}
