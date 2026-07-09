using AngleSharp.Html.Parser;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches nyaa.si. Scrapes the HTML search results page rather than the RSS feed: the RSS
/// endpoint silently ignores the "sort by seeders" query params and always returns a fixed-size
/// (75 item) window in some other default order — so a popular, heavily-seeded torrent can be
/// completely absent from the RSS results while still showing up on the actual website. The HTML
/// page correctly honours "sort by seeders descending", matching what a user sees in a browser.
/// </summary>
public class NyaaProvider : ITorrentSearchProvider
{
    public const string ProviderName = "Nyaa";
    public string Name => ProviderName;

    private static readonly string[] Trackers =
    {
        "http://nyaa.tracker.wf:7777/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://exodus.desync.com:6969/announce"
    };

    private static readonly HtmlParser Parser = new();

    // Nyaa is anime-focused and needs its own tracker in addition to the generic public ones.

    private readonly IHttpClientFactory _httpClientFactory;

    public NyaaProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        // f=0 (no filter), c=0_0 (all categories) — same scope the RSS feed used to cover.
        var url = $"https://nyaa.si/?f=0&c=0_0&q={Uri.EscapeDataString(query)}&s=seeders&o=desc";
        var html = await http.GetStringAsync(url, ct);
        var document = Parser.ParseDocument(html);

        var results = new List<TorrentSearchResult>();
        foreach (var row in document.QuerySelectorAll("tr"))
        {
            var titleLink = row.QuerySelector("a[href^='/view/']");
            var magnetHref = row.QuerySelector("a[href^='magnet:']")?.GetAttribute("href");
            if (titleLink is null || magnetHref is null)
                continue; // header row or a malformed entry

            var hash = Magnet.ExtractInfoHash(magnetHref);
            if (string.IsNullOrEmpty(hash))
                continue;

            var centerCells = row.QuerySelectorAll("td.text-center").ToList();
            if (centerCells.Count < 5)
                continue; // links, size, date, seeders, leechers

            var name = titleLink.TextContent.Trim();
            var timestampAttr = row.QuerySelector("td[data-timestamp]")?.GetAttribute("data-timestamp");
            var detailPath = titleLink.GetAttribute("href") ?? "";

            results.Add(new TorrentSearchResult
            {
                Title = name,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, name, Trackers),
                SizeBytes = ByteSize.Parse(centerCells[1].TextContent.Trim()),
                Seeders = int.TryParse(centerCells[3].TextContent.Trim(), out var s) ? s : 0,
                Leechers = int.TryParse(centerCells[4].TextContent.Trim(), out var l) ? l : 0,
                PublishedAt = long.TryParse(timestampAttr, out var ts)
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
                    : null,
                Source = Name,
                DetailsUrl = $"https://nyaa.si{detailPath}"
            });
        }
        return results;
    }
}
