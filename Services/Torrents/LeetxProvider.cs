using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches 1337x. The main domains sit behind a Cloudflare challenge, so this uses public
/// mirrors that serve plain HTML. The results list carries everything needed to show a row
/// (title, size, seeders, leechers, date) but not the magnet link or description — those live
/// only on each torrent's detail page. Rather than fetching every detail page during the search
/// (one HTTP request per row), this provider implements <see cref="ITorrentDetailsProvider"/> so
/// the detail page is fetched lazily, once, only for the specific torrent the user opens.
/// </summary>
public class LeetxProvider : ITorrentSearchProvider, ITorrentDetailsProvider
{
    public const string ProviderName = "1337x";
    public string Name => ProviderName;

    private static readonly string[] Mirrors = { "www.1377x.to", "www.1337xx.to" };

    /// <summary>Index into <see cref="Mirrors"/> of the mirror that last succeeded.</summary>
    private static volatile int _preferredMirror;

    private static readonly HtmlParser Parser = new();

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
        foreach (var row in document.QuerySelectorAll("tbody tr"))
        {
            // The name cell has two anchors: an icon-only category link and the actual title link
            // (the icon anchor has no text content, so filtering on that picks the right one).
            var titleLink = row.QuerySelectorAll("td.coll-1 a[href^='/torrent/']")
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.TextContent));
            if (titleLink is null)
                continue;

            var seeds = row.QuerySelector("td.coll-2");
            var leech = row.QuerySelector("td.coll-3");
            var size = row.QuerySelector("td.coll-4");
            var date = row.QuerySelector("td.coll-date");

            yield return new Row(
                DetailPath: titleLink.GetAttribute("href") ?? "",
                Title: titleLink.TextContent.Trim(),
                SizeBytes: size is not null ? ByteSize.Parse(size.TextContent.Trim()) : 0,
                Seeders: seeds is not null && int.TryParse(seeds.TextContent.Trim(), out var s) ? s : 0,
                Leechers: leech is not null && int.TryParse(leech.TextContent.Trim(), out var l) ? l : 0,
                Published: date is not null ? ParseDate(date.TextContent.Trim()) : null);
        }
    }

    private TorrentSearchResult ToResult(string host, Row row) => new()
    {
        Title = row.Title,
        // No magnet/hash until the detail page is resolved on demand; a stable placeholder keeps
        // cross-provider de-duplication working in the meantime.
        InfoHash = $"1337x-{ExtractId(row.DetailPath)}",
        SizeBytes = row.SizeBytes,
        Seeders = row.Seeders,
        Leechers = row.Leechers,
        PublishedAt = row.Published,
        Source = Name,
        DetailsUrl = $"https://{host}{row.DetailPath}"
    };

    /// <summary>Extracts the numeric torrent id from a detail path like "/torrent/6300307/slug/".</summary>
    private static string ExtractId(string detailPath)
    {
        var parts = detailPath.Trim('/').Split('/');
        return parts.Length > 1 ? parts[1] : detailPath;
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
