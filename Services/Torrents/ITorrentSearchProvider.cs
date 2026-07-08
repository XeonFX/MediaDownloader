namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Abstraction for a torrent site. Implement this interface and register it in
/// Program.cs (builder.Services.AddSingleton&lt;ITorrentSearchProvider, MyProvider&gt;())
/// to add a new torrent source.
/// </summary>
public interface ITorrentSearchProvider
{
    /// <summary>Unique display name of the source, e.g. "The Pirate Bay".</summary>
    string Name { get; }

    Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default);
}
