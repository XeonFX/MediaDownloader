using AngleSharp.Html.Parser;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// A parsed search-results row from a "list only" scraper: every column except the magnet link and
/// description, which live solely on each torrent's detail page.
/// </summary>
internal sealed record ScrapedRow(string DetailPath, string Title, long SizeBytes, int Seeders, int Leechers, DateTime? Published);

/// <summary>
/// Plumbing for providers that scrape a search-results list carrying every column but the magnet
/// link and description — those are fetched lazily, once, from the detail page via
/// <see cref="ITorrentDetailsProvider"/>. Only 1337x needs this now: RARBG used to as well, but it
/// moved to TheRARBG's JSON API, which serves real info hashes in the listing itself.
/// </summary>
internal static class LazyDetailScraper
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Maps a parsed list row to a search result. <paramref name="placeholderHash"/> stands in for
    /// the real info hash until the detail page is resolved on demand — a stable value keeps
    /// cross-provider de-duplication working in the meantime.
    /// </summary>
    public static TorrentSearchResult ToResult(string host, ScrapedRow row, string placeholderHash, string source) => new()
    {
        Title = row.Title,
        InfoHash = placeholderHash,
        SizeBytes = row.SizeBytes,
        Seeders = row.Seeders,
        Leechers = row.Leechers,
        PublishedAt = row.Published,
        Source = source,
        DetailsUrl = $"https://{host}{row.DetailPath}"
    };

    /// <summary>
    /// Fetches a result's detail page and extracts the magnet link (rebuilt from its info hash with
    /// our own trackers) and description. Both providers expose the magnet as the first
    /// <c>a[href^='magnet:']</c> and the description in a <c>#description</c> element.
    /// </summary>
    public static async Task<TorrentDetails> ResolveDetailsAsync(HttpClient http, TorrentSearchResult result, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(result.DetailsUrl))
            return new TorrentDetails();

        var detailHtml = await http.GetStringAsync(result.DetailsUrl, ct);
        var document = Parser.ParseDocument(detailHtml);

        string? hash = null, magnetUri = null;
        var magnetHref = document.QuerySelector("a[href^='magnet:']")?.GetAttribute("href");
        if (magnetHref is not null)
        {
            hash = Magnet.ExtractInfoHash(magnetHref);
            if (!string.IsNullOrEmpty(hash))
                magnetUri = Magnet.Build(hash, result.Title);
        }

        var description = document.QuerySelector("#description") is { } descElement
            ? DescriptionExtractor.ToPlainText(descElement.InnerHtml)
            : null;

        return new TorrentDetails { InfoHash = hash, MagnetUri = magnetUri, Description = description };
    }
}
