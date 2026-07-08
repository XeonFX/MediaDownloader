using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches The Pirate Bay. Prefers the apibay.org JSON API and falls back to HTML mirrors
/// (some ISPs block the official domains at the network level). Whichever source last
/// succeeded is tried first on subsequent searches.
/// </summary>
public class PirateBayProvider : ITorrentSearchProvider
{
    public const string ProviderName = "The Pirate Bay";
    public string Name => ProviderName;

    private static readonly (string Host, bool IsHtmlMirror)[] Sources =
    {
        ("apibay.org", false),
        ("tpb.party", true),
        ("piratebay.live", true)
    };

    /// <summary>Index into <see cref="Sources"/> of the source that last succeeded.</summary>
    private static volatile int _preferredSource;

    private readonly IHttpClientFactory _httpClientFactory;

    public PirateBayProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        var start = _preferredSource;
        Exception? lastError = null;

        for (var i = 0; i < Sources.Length; i++)
        {
            var index = (start + i) % Sources.Length;
            var (host, isHtmlMirror) = Sources[index];
            var url = isHtmlMirror
                ? $"https://{host}/search/{Uri.EscapeDataString(query)}/1/99/0"
                : $"https://{host}/q.php?q={Uri.EscapeDataString(query)}";
            try
            {
                var content = await http.GetStringAsync(url, ct);
                var results = isHtmlMirror ? ParseMirrorHtml(content) : ParseApi(content);
                _preferredSource = index;
                return results;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw lastError!;
    }

    private IReadOnlyList<TorrentSearchResult> ParseApi(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var results = new List<TorrentSearchResult>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            var name = item.GetProperty("name").GetString() ?? "";
            if (id == "0" || name == "No results returned")
                continue; // apibay returns a single placeholder row when nothing is found

            var hash = item.GetProperty("info_hash").GetString() ?? "";
            var added = GetLong(item, "added");
            results.Add(new TorrentSearchResult
            {
                Title = name,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, name),
                SizeBytes = GetLong(item, "size"),
                Seeders = (int)GetLong(item, "seeders"),
                Leechers = (int)GetLong(item, "leechers"),
                PublishedAt = added > 0 ? DateTimeOffset.FromUnixTimeSeconds(added).UtcDateTime : null,
                Source = Name
            });
        }
        return results;
    }

    // The mirrors serve the classic TPB result table: one <tr> per torrent with a magnet
    // anchor, a "Details for …" title link, a plain-text uploaded cell and three
    // right-aligned cells (size, seeders, leechers).
    private static readonly Regex RowRegex = new(@"<tr(?:\s[^>]*)?>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MagnetHashRegex = new(@"magnet:\?xt=urn:btih:([0-9A-Fa-f]{40})", RegexOptions.Compiled);
    private static readonly Regex DetailsTitleRegex = new(@"title=""Details for ([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex PlainCellRegex = new(@"<td>([^<]+)</td>", RegexOptions.Compiled);
    private static readonly Regex RightCellRegex = new(@"<td align=""right"">([^<]*)</td>", RegexOptions.Compiled);

    private IReadOnlyList<TorrentSearchResult> ParseMirrorHtml(string html)
    {
        var results = new List<TorrentSearchResult>();
        foreach (Match row in RowRegex.Matches(html))
        {
            var cells = row.Groups[1].Value;
            var hashMatch = MagnetHashRegex.Match(cells);
            if (!hashMatch.Success)
                continue; // header/pagination row

            var hash = hashMatch.Groups[1].Value;
            var titleMatch = DetailsTitleRegex.Match(cells);
            var title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value);
            var uploaded = PlainCellRegex.Match(cells);
            var numeric = RightCellRegex.Matches(cells);

            results.Add(new TorrentSearchResult
            {
                Title = title,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, title),
                SizeBytes = numeric.Count > 0 ? ByteSize.Parse(WebUtility.HtmlDecode(numeric[0].Groups[1].Value)) : 0,
                Seeders = numeric.Count > 1 && int.TryParse(numeric[1].Groups[1].Value, out var s) ? s : 0,
                Leechers = numeric.Count > 2 && int.TryParse(numeric[2].Groups[1].Value, out var l) ? l : 0,
                PublishedAt = uploaded.Success ? ParseUploaded(WebUtility.HtmlDecode(uploaded.Groups[1].Value)) : null,
                Source = Name
            });
        }
        return results;
    }

    /// <summary>Parses TPB upload cells: "04-25 16:35" (current year), "09-08 2024", "Today 16:35", "Y-day 16:35".</summary>
    private static DateTime? ParseUploaded(string text)
    {
        // Mirror cells separate fields with &nbsp;, which decodes to U+00A0.
        text = text.Replace(' ', ' ').Trim();
        var today = DateTime.UtcNow.Date;
        if (text.StartsWith("Today", StringComparison.OrdinalIgnoreCase))
            return today;
        if (text.StartsWith("Y-day", StringComparison.OrdinalIgnoreCase))
            return today.AddDays(-1);
        if (DateTime.TryParseExact(text, "MM-dd yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParseExact(text, "MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return new DateTime(today.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc);
        return null;
    }

    /// <summary>apibay sometimes returns numbers as JSON strings — accept both.</summary>
    private static long GetLong(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String => long.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0
        };
    }
}
