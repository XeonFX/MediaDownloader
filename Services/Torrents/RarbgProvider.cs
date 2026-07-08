using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches RARBG through the rarbggo.to proxy. Like 1337x, the results list carries no magnet
/// link or description — each torrent's detail page does. Rather than fetching every detail page
/// during the search (one HTTP request per row), this provider implements
/// <see cref="ITorrentDetailsProvider"/> so the detail page is fetched lazily, once, only for the
/// specific torrent the user opens.
/// </summary>
public class RarbgProvider : ITorrentSearchProvider, ITorrentDetailsProvider
{
    public const string ProviderName = "RARBG";
    public string Name => ProviderName;

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
    private static readonly Regex DescriptionRegex = new(@"id=""description""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);

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
                var results = ParseRows(listHtml).Select(r => ToResult(host, r)).ToList();
                _preferredMirror = index;
                return results;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw lastError!;
    }

    /// <summary>Fetches the detail page for one result to resolve its magnet link and description.</summary>
    public async Task<TorrentDetails> GetDetailsAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(result.DetailsUrl))
            return new TorrentDetails();

        var http = _httpClientFactory.CreateClient("torrent-search");
        var detailHtml = await http.GetStringAsync(result.DetailsUrl, ct);

        string? hash = null, magnetUri = null;
        var magnet = MagnetRegex.Match(detailHtml);
        if (magnet.Success)
        {
            hash = Magnet.ExtractInfoHash(magnet.Groups[1].Value);
            if (!string.IsNullOrEmpty(hash))
                magnetUri = Magnet.Build(hash, result.Title);
        }

        var description = DescriptionRegex.Match(detailHtml) is { Success: true } m
            ? DescriptionExtractor.ToPlainText(m.Groups[1].Value)
            : null;

        return new TorrentDetails { InfoHash = hash, MagnetUri = magnetUri, Description = description };
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

    private TorrentSearchResult ToResult(string host, Row row) => new()
    {
        Title = row.Title,
        // No magnet/hash until the detail page is resolved on demand; a stable placeholder keeps
        // cross-provider de-duplication working in the meantime.
        InfoHash = $"rarbg-{ExtractId(row.DetailPath)}",
        SizeBytes = row.SizeBytes,
        Seeders = row.Seeders,
        Leechers = row.Leechers,
        PublishedAt = row.Published,
        Source = Name,
        DetailsUrl = $"https://{host}{row.DetailPath}"
    };

    /// <summary>Extracts the numeric torrent id from a detail path like "/torrent/some-slug-2099267.html".</summary>
    private static string ExtractId(string detailPath)
    {
        var name = detailPath.Split('/').Last().Replace(".html", "");
        var dash = name.LastIndexOf('-');
        return dash >= 0 ? name[(dash + 1)..] : name;
    }
}
