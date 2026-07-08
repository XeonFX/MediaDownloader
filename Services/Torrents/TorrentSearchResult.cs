namespace MediaDownloader.Services.Torrents;

public class TorrentSearchResult
{
    public string Title { get; set; } = string.Empty;
    public string MagnetUri { get; set; } = string.Empty;
    public string InfoHash { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime? PublishedAt { get; set; }

    public string SizeDisplay => ByteSize.Format(SizeBytes, decimals: 2);
}
