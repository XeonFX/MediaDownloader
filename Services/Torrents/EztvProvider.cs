using System.Text.Json;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches eztvx.to (EZTV), a TV-only tracker. Its HTML search page sits behind a Cloudflare JS
/// challenge that a plain HttpClient can't pass (confirmed: every mirror redirects back to
/// eztvx.to and gets the same "cf-mitigated: challenge" response), and the public
/// /api/get-torrents endpoint has no keyword filter — only imdb_id/hash. So this provider pages
/// through the API's newest-first firehose of all torrents and keeps only rows whose title matches
/// the query, capped at <see cref="MaxPages"/> pages. That means it only ever finds torrents among
/// the most recent releases; older or low-activity shows won't turn up even if EZTV has them.
/// </summary>
public class EztvProvider : ITorrentSearchProvider
{
    public const string ProviderName = "EZTV";
    public string Name => ProviderName;

    private const string BaseUrl = "https://eztvx.to";
    private const int PageSize = 100;
    private const int MaxPages = 5;
    private const int MaxResults = 50;

    private readonly IHttpClientFactory _httpClientFactory;

    public EztvProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        var matches = new List<TorrentSearchResult>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var url = $"{BaseUrl}/api/get-torrents?page={page}&limit={PageSize}";
            var json = await http.GetStringAsync(url, ct);
            var parsed = ParsePage(json);
            matches.AddRange(parsed.Torrents.Where(t => SearchRelevance.Matches(query, t.Title)));

            var scanned = (long)page * PageSize;
            if (matches.Count >= MaxResults || parsed.Torrents.Count == 0 || scanned >= parsed.TotalCount)
                break;
        }

        return matches.Count > MaxResults ? matches[..MaxResults] : matches;
    }

    internal readonly record struct EztvPage(IReadOnlyList<TorrentSearchResult> Torrents, long TotalCount);

    /// <summary>Parses one /api/get-torrents page. Internal so fixture-based tests can exercise it without a live HTTP call.</summary>
    internal static EztvPage ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var totalCount = root.TryGetProperty("torrents_count", out var tc) ? tc.GetInt64() : 0;

        var results = new List<TorrentSearchResult>();
        if (!root.TryGetProperty("torrents", out var torrents))
            return new EztvPage(results, totalCount);

        foreach (var item in torrents.EnumerateArray())
        {
            // Unlike the private-tracker providers, EZTV's API hands back a real BitTorrent info
            // hash directly — no need for a synthesized placeholder or a lazy detail-page fetch.
            var hash = item.TryGetProperty("hash", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(hash))
                continue;

            var sizeText = item.TryGetProperty("size_bytes", out var sb) ? sb.GetString() : null;
            var released = item.TryGetProperty("date_released_unix", out var dr) ? dr.GetInt64() : 0;

            results.Add(new TorrentSearchResult
            {
                Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                InfoHash = hash,
                MagnetUri = item.TryGetProperty("magnet_url", out var m) ? m.GetString() ?? "" : "",
                SizeBytes = long.TryParse(sizeText, out var size) ? size : 0,
                Seeders = item.TryGetProperty("seeds", out var se) ? se.GetInt32() : 0,
                Leechers = item.TryGetProperty("peers", out var pe) ? pe.GetInt32() : 0,
                PublishedAt = released > 0 ? DateTimeOffset.FromUnixTimeSeconds(released).UtcDateTime : null,
                Source = ProviderName
            });
        }
        return new EztvPage(results, totalCount);
    }
}
