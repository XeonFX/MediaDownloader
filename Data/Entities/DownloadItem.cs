using System.ComponentModel.DataAnnotations.Schema;

namespace MediaDownloader.Data.Entities;

public enum DownloadStatus
{
    Queued,
    FetchingMetadata,
    Downloading,
    Seeding,
    Paused,
    Completed,
    Error
}

public class DownloadItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MagnetUri { get; set; } = string.Empty;

    /// <summary>
    /// Path to a locally cached .torrent file. Set (instead of a magnet) for downloads from
    /// private trackers; the engine loads metadata from this file rather than the DHT.
    /// </summary>
    public string? TorrentFilePath { get; set; }

    public string InfoHash { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public double Progress { get; set; }
    public long TotalBytes { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public bool StartNotificationSent { get; set; }
    public bool CompleteNotificationSent { get; set; }

    public int? SeriesTaskId { get; set; }
    public SeriesTask? SeriesTask { get; set; }

    // Runtime-only stats (not persisted)
    [NotMapped] public long DownloadSpeed { get; set; }
    [NotMapped] public long UploadSpeed { get; set; }
    [NotMapped] public int Peers { get; set; }
}
