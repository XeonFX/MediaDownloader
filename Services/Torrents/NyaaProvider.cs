using System.Net;
using System.Text.RegularExpressions;

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

    private static readonly Regex RowRegex = new(@"<tr class=""[^""]*"">(.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(@"href=""/view/(\d+)""\s+title=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex MagnetRegex = new(@"href=""(magnet:\?[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex TimestampRegex = new(@"data-timestamp=""(\d+)""", RegexOptions.Compiled);
    private static readonly Regex CenterCellRegex = new(@"<td class=""text-center""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);

    // Nyaa is anime-focused and needs its own tracker in addition to the generic public ones.

    private readonly IHttpClientFactory _httpClientFactory;

    public NyaaProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        // f=0 (no filter), c=0_0 (all categories) — same scope the RSS feed used to cover.
        var url = $"https://nyaa.si/?f=0&c=0_0&q={Uri.EscapeDataString(query)}&s=seeders&o=desc";
        var html = await http.GetStringAsync(url, ct);

        var results = new List<TorrentSearchResult>();
        foreach (Match row in RowRegex.Matches(html))
        {
            var cells = row.Groups[1].Value;
            var title = TitleRegex.Match(cells);
            var magnet = MagnetRegex.Match(cells);
            if (!title.Success || !magnet.Success)
                continue; // header row or a malformed entry

            var hash = Magnet.ExtractInfoHash(WebUtility.HtmlDecode(magnet.Groups[1].Value));
            if (string.IsNullOrEmpty(hash))
                continue;

            var centerCells = CenterCellRegex.Matches(cells);
            if (centerCells.Count < 5)
                continue; // links, size, date, seeders, leechers

            var name = WebUtility.HtmlDecode(title.Groups[2].Value);
            var timestamp = TimestampRegex.Match(cells);

            results.Add(new TorrentSearchResult
            {
                Title = name,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, name, Trackers),
                SizeBytes = ByteSize.Parse(centerCells[1].Groups[1].Value.Trim()),
                Seeders = int.TryParse(centerCells[3].Groups[1].Value.Trim(), out var s) ? s : 0,
                Leechers = int.TryParse(centerCells[4].Groups[1].Value.Trim(), out var l) ? l : 0,
                PublishedAt = timestamp.Success && long.TryParse(timestamp.Groups[1].Value, out var ts)
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
                    : null,
                Source = Name,
                DetailsUrl = $"https://nyaa.si/view/{title.Groups[1].Value}"
            });
        }
        return results;
    }
}
