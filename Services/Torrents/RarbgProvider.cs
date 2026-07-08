using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches RARBG through the rarbggo.to proxy. Like 1337x, the results list carries no magnet
/// links — each torrent's detail page does — so a search is a two-step scrape: fetch the
/// (seeder-sorted) list, then fetch the detail pages for the top results in parallel.
/// </summary>
public class RarbgProvider : ITorrentSearchProvider
{
    public const string ProviderName = "RARBG";
    public string Name => ProviderName;

    /// <summary>Cap on detail-page fetches per search — one HTTP request each, so keep it modest.</summary>
    private const int MaxDetailFetches = 12;

    private static readonly string[] Mirrors = { "www2.rarbggo.to", "rarbggo.to" };

    /// <summary>Index into <see cref="Mirrors"/> of the mirror that last succeeded.</summary>
    private static volatile int _preferredMirror;

    private static readonly Regex RowSplitRegex = new(@"<tr class=""table2ta"">", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"href=""(/torrent/[^""]+)""[^>]*>([^<]+)</a>", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@">(\d{4}-\d{2}-\d{2}) \d{2}:\d{2}:\d{2}<", RegexOptions.Compiled);
    private static readonly Regex SizeRegex = new(@"width=""100px""[^>]*>([^<]+)</td>", RegexOptions.Compiled);
    // Seeders then leechers sit in the two width="50px" cells; seeders are wrapped in a <font> tag.
    private static readonly Regex SeedLeechRegex = new(@"width=""50px""[^>]*>\s*(?:<font[^>]*>)?(\d+)", RegexOptions.Compiled);
    private static readonly Regex MagnetRegex = new(@"href=""(magnet:\?[^""]+)""", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;

    public RarbgProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

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
                var url = $"https://{host}/search/?search={Uri.EscapeDataString(query)}&order=seeders&by=DESC";
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
        // Skip everything before the first result row (chunk 0 is the page header/nav).
        foreach (var chunk in RowSplitRegex.Split(html).Skip(1))
        {
            var link = LinkRegex.Match(chunk);
            if (!link.Success)
                continue;

            var date = DateRegex.Match(chunk);
            var size = SizeRegex.Match(chunk);
            var seedLeech = SeedLeechRegex.Matches(chunk);

            yield return new Row(
                DetailPath: link.Groups[1].Value,
                Title: WebUtility.HtmlDecode(link.Groups[2].Value).Trim(),
                SizeBytes: size.Success ? ByteSize.Parse(WebUtility.HtmlDecode(size.Groups[1].Value)) : 0,
                Seeders: seedLeech.Count > 0 ? int.Parse(seedLeech[0].Groups[1].Value) : 0,
                Leechers: seedLeech.Count > 1 ? int.Parse(seedLeech[1].Groups[1].Value) : 0,
                Published: date.Success
                    ? DateTime.ParseExact(date.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null);
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
                Source = Name,
                DetailsUrl = $"https://{host}{row.DetailPath}"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null; // one dead detail page shouldn't sink the whole search
        }
    }
}
