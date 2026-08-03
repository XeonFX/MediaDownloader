using System.Text.Json;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches the RARBG catalogue through TheRARBG (therarbg.com), the archive that took over after
/// RARBG itself shut down in 2023.
///
/// This used to scrape the rarbggo.to proxy, but both of its mirrors now sit behind a Cloudflare JS
/// challenge (verified: every request returns 403 "Just a moment...", with or without a browser
/// User-Agent), which a plain HttpClient cannot pass — the provider was returning nothing at all.
/// TheRARBG exposes a plain JSON endpoint instead, and unlike the old HTML listing it hands back a
/// real BitTorrent info hash per row, so magnets are built directly during the search and no
/// per-torrent detail fetch is needed to start a download. Only the description still lives on the
/// detail endpoint, so that alone is resolved lazily via <see cref="ITorrentDetailsProvider"/>.
/// </summary>
public class RarbgProvider : ITorrentSearchProvider, ITorrentDetailsProvider
{
    // Kept as "RARBG" rather than "TheRARBG": the name is the key for the per-provider enable
    // toggle and credential rows in Settings, so renaming it would silently reset the user's choice.
    public const string ProviderName = "RARBG";
    public string Name => ProviderName;

    private const string BaseUrl = "https://therarbg.com";

    /// <summary>Rows per page the endpoint returns; used to decide whether a second page is worth fetching.</summary>
    private const int PageSize = 50;

    /// <summary>
    /// The endpoint has no "sort by seeders" parameter (order=… is accepted but ignored), so results
    /// come back in its own order and are sorted client-side. Two pages is a deliberate cap: enough
    /// that a popular release isn't stranded on page 2, without turning one search into six requests.
    /// </summary>
    private const int MaxPages = 2;

    private readonly IHttpClientFactory _httpClientFactory;

    public RarbgProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");

        var first = ParsePage(await http.GetStringAsync(SearchUrl(query, page: 1), ct));
        if (first.Results.Count < PageSize || first.Total <= PageSize)
            return first.Results;

        // Page 1 came back full and the server reports more — fetch the rest concurrently rather
        // than walking pages one at a time, so extra depth costs latency only once.
        var rest = await Task.WhenAll(Enumerable.Range(2, MaxPages - 1).Select(async page =>
        {
            try
            {
                return ParsePage(await http.GetStringAsync(SearchUrl(query, page), ct)).Results;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                return []; // a failed extra page shouldn't lose the page-1 results we already have
            }
        }));

        return first.Results.Concat(rest.SelectMany(r => r)).ToList();
    }

    private static string SearchUrl(string query, int page)
    {
        // Keywords are a path segment, not a query-string value: /get-posts/keywords:<query>/
        var url = $"{BaseUrl}/get-posts/keywords:{Uri.EscapeDataString(query)}/?format=json";
        return page > 1 ? $"{url}&page={page}" : url;
    }

    /// <summary>Fetches the description for one result; the search listing never carries it.</summary>
    public async Task<TorrentDetails> GetDetailsAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(result.DetailsUrl))
            return new TorrentDetails();

        var http = _httpClientFactory.CreateClient("torrent-search");
        string json;
        try
        {
            json = await http.GetStringAsync($"{result.DetailsUrl}?format=json", ct);
        }
        catch (HttpRequestException)
        {
            return new TorrentDetails();
        }

        return ParseDetail(json);
    }

    internal readonly record struct RarbgPage(IReadOnlyList<TorrentSearchResult> Results, long Total);

    /// <summary>Parses one search page. Internal so fixture-based tests can exercise it without a live HTTP call.</summary>
    internal static RarbgPage ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;

        var results = new List<TorrentSearchResult>();
        if (!root.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return new RarbgPage(results, total);

        foreach (var item in rows.EnumerateArray())
        {
            // Field names are single letters: n=name, h=info hash, s=size, se=seeders, le=leechers,
            // a=added (unix seconds), pk=detail id.
            var hash = GetString(item, "h");
            var name = GetString(item, "n") ?? "";
            if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(name))
                continue; // no hash means no usable magnet, and dedup elsewhere keys off this field

            var added = GetLong(item, "a");
            var pk = GetString(item, "pk");

            results.Add(new TorrentSearchResult
            {
                Title = name,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, name),
                SizeBytes = GetLong(item, "s"),
                Seeders = ToInt(GetLong(item, "se")),
                Leechers = ToInt(GetLong(item, "le")),
                PublishedAt = added > 0 ? DateTimeOffset.FromUnixTimeSeconds(added).UtcDateTime : null,
                Source = ProviderName,
                // The slug segment is required by the route but not validated — any placeholder works,
                // and the listing doesn't carry the real one.
                DetailsUrl = pk is null ? null : $"{BaseUrl}/post-detail/{pk}/x/"
            });
        }
        return new RarbgPage(results, total);
    }

    /// <summary>Parses a detail response. Internal so fixture-based tests can exercise it without a live HTTP call.</summary>
    internal static TorrentDetails ParseDetail(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var description = GetString(doc.RootElement, "descr");
        return new TorrentDetails
        {
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
        };
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Sizes and counts come back as numbers, but tolerate the string form too.</summary>
    private static long GetLong(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    /// <summary>Clamps a peer count into int range so a malformed huge value can't wrap negative.</summary>
    private static int ToInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
