using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

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

    private static readonly HtmlParser Parser = new();
    private static readonly Regex DateCellRegex = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", RegexOptions.Compiled);

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

    private sealed record Row(string DetailPath, string Title, long SizeBytes, int Seeders, int Leechers, DateTime? Published);

    private static IEnumerable<Row> ParseRows(string html)
    {
        var document = Parser.ParseDocument(html);
        foreach (var row in document.QuerySelectorAll("tr.table2ta"))
        {
            var link = row.QuerySelector("a[href^='/torrent/']");
            if (link is null)
                continue;

            // Size and the seeders/leechers pair are identified by their fixed column widths;
            // seeders is wrapped in a <font> tag, leechers isn't. The date cell has no distinguishing
            // class/attribute, so it's picked out by matching its text against the known format.
            var sizeCell = row.QuerySelector("td[width='100px']");
            var seedLeechCells = row.QuerySelectorAll("td[width='50px']").ToList();
            var dateCell = row.QuerySelectorAll("td").FirstOrDefault(td => DateCellRegex.IsMatch(td.TextContent.Trim()));

            yield return new Row(
                DetailPath: link.GetAttribute("href") ?? "",
                Title: link.TextContent.Trim(),
                SizeBytes: sizeCell is not null ? ByteSize.Parse(sizeCell.TextContent.Trim()) : 0,
                Seeders: seedLeechCells.Count > 0 && int.TryParse(seedLeechCells[0].TextContent.Trim(), out var s) ? s : 0,
                Leechers: seedLeechCells.Count > 1 && int.TryParse(seedLeechCells[1].TextContent.Trim(), out var l) ? l : 0,
                Published: dateCell is not null
                    ? DateTime.ParseExact(dateCell.TextContent.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
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
