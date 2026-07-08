using System.Text.Json;

namespace MediaDownloader.Services.Torrents;

/// <summary>Searches the torrents-csv.com open index (aggregates The Pirate Bay and other sources).</summary>
public class TorrentsCsvProvider : ITorrentSearchProvider
{
    public const string ProviderName = "Torrents-CSV";
    public string Name => ProviderName;

    private readonly IHttpClientFactory _httpClientFactory;

    public TorrentsCsvProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        var url = $"https://torrents-csv.com/service/search?q={Uri.EscapeDataString(query)}&size=100";
        using var stream = await http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<TorrentSearchResult>();
        if (!doc.RootElement.TryGetProperty("torrents", out var torrents))
            return results;

        foreach (var item in torrents.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var hash = item.GetProperty("infohash").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(hash))
                continue;

            var created = item.TryGetProperty("created_unix", out var c) ? c.GetInt64() : 0;
            results.Add(new TorrentSearchResult
            {
                Title = name,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, name),
                SizeBytes = item.TryGetProperty("size_bytes", out var s) ? s.GetInt64() : 0,
                Seeders = item.TryGetProperty("seeders", out var se) ? se.GetInt32() : 0,
                Leechers = item.TryGetProperty("leechers", out var le) ? le.GetInt32() : 0,
                PublishedAt = created > 0 ? DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime : null,
                Source = Name
            });
        }
        return results;
    }
}
