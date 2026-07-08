namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Abstraction for a torrent site. Implement this interface and the provider is picked up
/// automatically: Program.cs scans the assembly for implementations and registers each one,
/// so it appears in the Search source list and in Settings without any extra wiring.
/// </summary>
public interface ITorrentSearchProvider
{
    /// <summary>Unique display name of the source, e.g. "The Pirate Bay".</summary>
    string Name { get; }

    /// <summary>
    /// True when the source needs a stored account (username/password). Settings shows credential
    /// fields for such providers, and searches skip them until credentials are saved.
    /// </summary>
    bool RequiresCredentials => false;

    Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Optional capability for providers whose results are fetched as .torrent files rather than
/// magnet links (typical for private trackers, where DHT is disabled and the tracker requires
/// an authenticated download). Results from such providers set
/// <see cref="TorrentSearchResult.TorrentFileUrl"/>.
/// </summary>
public interface ITorrentFileSource
{
    /// <summary>Downloads the .torrent file for a search result produced by this provider.</summary>
    Task<byte[]> DownloadTorrentFileAsync(TorrentSearchResult result, CancellationToken ct = default);
}

/// <summary>
/// Optional capability for providers whose search listing omits info that costs an extra page
/// load per torrent — a magnet link only resolvable from a detail page, and/or a description.
/// <see cref="TorrentSearchService.EnsureDetailsAsync"/> calls this on demand (when the info
/// dialog opens or a download is started for one specific result) — never during
/// <see cref="ITorrentSearchProvider.SearchAsync"/>, so a search never fires one HTTP request
/// per result.
/// </summary>
public interface ITorrentDetailsProvider
{
    Task<TorrentDetails> GetDetailsAsync(TorrentSearchResult result, CancellationToken ct = default);
}

/// <summary>
/// Extra per-torrent info fetched lazily by <see cref="ITorrentDetailsProvider"/>. Null/empty
/// fields mean "nothing new to report" — callers keep whatever the result already had.
/// </summary>
public class TorrentDetails
{
    public string? InfoHash { get; init; }
    public string? MagnetUri { get; init; }
    public string? Description { get; init; }
}
