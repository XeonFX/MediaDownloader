using System.Globalization;
using System.Xml.Linq;

namespace MediaDownloader.Services.Torrents;

/// <summary>Searches nyaa.si through its RSS feed.</summary>
public class NyaaProvider : ITorrentSearchProvider
{
    public const string ProviderName = "Nyaa";
    public string Name => ProviderName;

    private static readonly XNamespace NyaaNs = "https://nyaa.si/xmlns/nyaa";

    private static readonly string[] Trackers =
    {
        "http://nyaa.tracker.wf:7777/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://exodus.desync.com:6969/announce"
    };

    private readonly IHttpClientFactory _httpClientFactory;

    // Nyaa is anime-focused and needs its own tracker in addition to the generic public ones.

    public NyaaProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        var url = $"https://nyaa.si/?page=rss&q={Uri.EscapeDataString(query)}";
        using var stream = await http.GetStreamAsync(url, ct);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);

        var results = new List<TorrentSearchResult>();
        foreach (var item in doc.Descendants("item"))
        {
            var title = item.Element("title")?.Value ?? "";
            var hash = item.Element(NyaaNs + "infoHash")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(hash))
                continue;

            results.Add(new TorrentSearchResult
            {
                Title = title,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, title, Trackers),
                SizeBytes = ByteSize.Parse(item.Element(NyaaNs + "size")?.Value),
                Seeders = int.TryParse(item.Element(NyaaNs + "seeders")?.Value, out var s) ? s : 0,
                Leechers = int.TryParse(item.Element(NyaaNs + "leechers")?.Value, out var l) ? l : 0,
                PublishedAt = DateTime.TryParse(item.Element("pubDate")?.Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
                Source = Name
            });
        }
        return results;
    }
}
