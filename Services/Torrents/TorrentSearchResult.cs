namespace MediaDownloader.Services.Torrents;

public class TorrentSearchResult
{
    public string Title { get; set; } = string.Empty;
    public string MagnetUri { get; set; } = string.Empty;

    /// <summary>
    /// Torrent info hash used for de-duplication across providers. Providers that don't expose a
    /// real hash (private trackers) fill in a unique placeholder such as "pte-12345" instead.
    /// </summary>
    public string InfoHash { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public int Seeders { get; set; }
    public int Leechers { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime? PublishedAt { get; set; }

    /// <summary>Web page with details for this torrent on the source site, when known.</summary>
    public string? DetailsUrl { get; set; }

    /// <summary>
    /// Set instead of <see cref="MagnetUri"/> when the source serves .torrent files (private
    /// trackers). The provider's <see cref="ITorrentFileSource"/> implementation downloads it.
    /// </summary>
    public string? TorrentFileUrl { get; set; }

    /// <summary>
    /// Full description text, when known. Null until fetched — some providers only expose this
    /// (and sometimes the magnet/hash) via a per-torrent detail page, resolved lazily through
    /// <see cref="ITorrentDetailsProvider"/> rather than during the search itself.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>True when neither a magnet nor a .torrent file URL is known yet — a download can't start until this result is resolved via <see cref="ITorrentDetailsProvider"/>.</summary>
    public bool NeedsResolution => string.IsNullOrEmpty(MagnetUri) && string.IsNullOrEmpty(TorrentFileUrl);

    public string SizeDisplay => ByteSize.Format(SizeBytes, decimals: 2);
}
