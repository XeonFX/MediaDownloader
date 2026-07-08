namespace MediaDownloader.Services.Torrents;

/// <summary>Holds the last search query and results so the Search page survives navigation.</summary>
public class SearchState
{
    public string Query { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;

    /// <summary>Resolution filter appended to the query ("" = any, else e.g. "1080p").</summary>
    public string Resolution { get; set; } = string.Empty;

    public List<TorrentSearchResult>? Results { get; set; }
}
