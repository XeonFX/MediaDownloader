using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches 1337x. The main domains sit behind a Cloudflare challenge, so this uses public
/// mirrors that serve plain HTML. 1337x lists magnets only on each torrent's detail page, so a
/// search is a two-step scrape: fetch the (seeder-sorted) results page, then fetch the detail
/// pages for the top results in parallel to collect their magnet links.
/// </summary>
public class LeetxProvider : ITorrentSearchProvider
{
    public const string ProviderName = "1337x";
    public string Name => ProviderName;

    /// <summary>Cap on detail-page fetches per search — one HTTP request each, so keep it modest.</summary>
    private const int MaxDetailFetches = 12;

    private static readonly string[] Mirrors = { "www.1377x.to", "www.1337xx.to" };

    /// <summary>Index into <see cref="Mirrors"/> of the mirror that last succeeded.</summary>
    private static volatile int _preferredMirror;

    private static readonly Regex RowRegex = new(@"<tr>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"href=""(/torrent/[^""]+)""[^>]*>([^<]+)</a>", RegexOptions.Compiled);
    private static readonly Regex SeedsRegex = new(@"coll-2 seeds"">(\d+)", RegexOptions.Compiled);
    private static readonly Regex LeechRegex = new(@"coll-3 leeches"">(\d+)", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"coll-date"">([^<]+)</td>", RegexOptions.Compiled);
    private static readonly Regex SizeRegex = new(@"coll-4 size[^""]*"">([^<]+)<", RegexOptions.Compiled);
    private static readonly Regex MagnetRegex = new(@"href=""(magnet:\?[^""]+)""", RegexOptions.Compiled);
    // Pulls "Jan 17 26" out of cells like "Jan. 17th  '26" or "5:42am Jan. 3rd '26".
    private static readonly Regex DatePartsRegex = new(@"([A-Za-z]{3,})\.?\s+(\d{1,2})(?:st|nd|rd|th)?\s+'?(\d{2})", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;

    public LeetxProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        var start = _preferredMirror;
        Exception? lastError = null;

        for (var i = 0; i < Mirrors.Length; i++)
        {
            var index = (start + i) % Mirrors.Length;
            var host = Mirrors[index];
            try
            {
                var url = $"https://{host}/sort-search/{Uri.EscapeDataString(query)}/seeders/desc/1/";
                var listHtml = await http.GetStringAsync(url, ct);
                var rows = ParseRows(listHtml).Take(MaxDetailFetches).ToList();

                var results = await Task.WhenAll(rows.Select(r => ResolveMagnetAsync(http, host, r, ct)));
                _preferredMirror = index;
                return results.Where(r => r is not null).ToList()!;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw lastError!;
    }

    private sealed record Row(string DetailPath, string Title, long SizeBytes, int Seeders, int Leechers, DateTime? Published);

    private static IEnumerable<Row> ParseRows(string html)
    {
        var tbody = html.IndexOf("<tbody>", StringComparison.Ordinal);
        if (tbody < 0) yield break;

        foreach (Match row in RowRegex.Matches(html[tbody..]))
        {
            var cells = row.Groups[1].Value;
            var link = LinkRegex.Match(cells);
            if (!link.Success)
                continue;

            var seeds = SeedsRegex.Match(cells);
            var leech = LeechRegex.Match(cells);
            var size = SizeRegex.Match(cells);
            var date = DateRegex.Match(cells);

            yield return new Row(
                DetailPath: link.Groups[1].Value,
                Title: WebUtility.HtmlDecode(link.Groups[2].Value).Trim(),
                SizeBytes: size.Success ? ByteSize.Parse(WebUtility.HtmlDecode(size.Groups[1].Value)) : 0,
                Seeders: seeds.Success ? int.Parse(seeds.Groups[1].Value) : 0,
                Leechers: leech.Success ? int.Parse(leech.Groups[1].Value) : 0,
                Published: date.Success ? ParseDate(WebUtility.HtmlDecode(date.Groups[1].Value)) : null);
        }
    }

    private async Task<TorrentSearchResult?> ResolveMagnetAsync(HttpClient http, string host, Row row, CancellationToken ct)
    {
        try
        {
            var detailHtml = await http.GetStringAsync($"https://{host}{row.DetailPath}", ct);
            var magnet = MagnetRegex.Match(detailHtml);
            if (!magnet.Success)
                return null;

            var hash = Magnet.ExtractInfoHash(magnet.Groups[1].Value);
            if (string.IsNullOrEmpty(hash))
                return null;

            return new TorrentSearchResult
            {
                Title = row.Title,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, row.Title),
                SizeBytes = row.SizeBytes,
                Seeders = row.Seeders,
                Leechers = row.Leechers,
                PublishedAt = row.Published,
                Source = Name
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null; // one dead detail page shouldn't sink the whole search
        }
    }

    /// <summary>Parses 1337x date cells such as "Jan. 17th '26", "Apr. 3rd '25" or "5:42am Jan. 3rd '26".</summary>
    private static DateTime? ParseDate(string text)
    {
        var m = DatePartsRegex.Match(text);
        if (!m.Success)
            return null;

        var normalized = $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}";
        return DateTime.TryParseExact(normalized, "MMM d yy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }
}
