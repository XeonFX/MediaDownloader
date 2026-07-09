using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

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

    private static readonly MirrorRotator<(string Host, bool IsHtmlMirror)> Sources = new(new[]
    {
        ("apibay.org", false),
        ("tpb.party", true),
        ("piratebay.live", true)
    });

    private readonly IHttpClientFactory _httpClientFactory;

    public PirateBayProvider(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("torrent-search");
        return Sources.FetchAsync<IReadOnlyList<TorrentSearchResult>>(async source =>
        {
            var (host, isHtmlMirror) = source;
            var url = isHtmlMirror
                ? $"https://{host}/search/{Uri.EscapeDataString(query)}/1/99/0"
                : $"https://{host}/q.php?q={Uri.EscapeDataString(query)}";
            var content = await http.GetStringAsync(url, ct);
            return isHtmlMirror ? ParseMirrorHtml(content) : ParseApi(content);
        }, ex => ex is HttpRequestException or TaskCanceledException or JsonException, ct);
    }

    /// <summary>Parses the apibay JSON response. Internal so fixture-based tests can exercise it without a live HTTP call.</summary>
    internal static IReadOnlyList<TorrentSearchResult> ParseApi(string json)
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
            if (string.IsNullOrWhiteSpace(hash))
                continue; // no hash means no usable magnet, and dedup elsewhere keys off this field

            var added = GetLong(item, "added");
            results.Add(new TorrentSearchResult
            {
                Title = name,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, name),
                SizeBytes = GetLong(item, "size"),
                Seeders = ToInt(GetLong(item, "seeders")),
                Leechers = ToInt(GetLong(item, "leechers")),
                PublishedAt = added > 0 ? DateTimeOffset.FromUnixTimeSeconds(added).UtcDateTime : null,
                Source = ProviderName
            });
        }
        return results;
    }

    private static readonly HtmlParser Parser = new();
    // The mirrors pack "Uploaded <date>, Size <size>, ULed by <uploader>" into a single detDesc
    // element rather than separate cells — pull the date/size sub-fields out of that string.
    private static readonly Regex DetDescRegex = new(@"Uploaded\s+([^,]+),\s*Size\s+([^,]+),", RegexOptions.Compiled);

    // The mirrors serve one of two classic-TPB table layouts (observed: piratebay.live serves a
    // compact "Single" view, tpb.party a "Double" view — verified directly, not documented
    // anywhere). "Double": title anchor, a plain date cell, a magnet anchor, then three
    // right-aligned cells (size, seeders, leechers). "Single": title anchor + magnet anchor +
    // a single <font class="detDesc"> blob ("Uploaded X, Size Y, ULed by Z"), then only two
    // right-aligned cells (seeders, leechers). Seeders/leechers are always the last two
    // right-aligned cells in either layout, so that part doesn't need to branch.
    private static readonly Regex UploadDateCellRegex = new(@"^(Today|Y-day|\d{2}-\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses an HTML-mirror search page. Internal so fixture-based tests can exercise it without a live HTTP call.</summary>
    internal static IReadOnlyList<TorrentSearchResult> ParseMirrorHtml(string html)
    {
        var document = Parser.ParseDocument(html);
        var results = new List<TorrentSearchResult>();

        foreach (var row in document.QuerySelectorAll("tr"))
        {
            var titleLink = row.QuerySelector("a[title^='Details for ']");
            var magnetHref = row.QuerySelector("a[href^='magnet:']")?.GetAttribute("href");
            if (titleLink is null || magnetHref is null)
                continue; // header/pagination row

            var hash = Magnet.ExtractInfoHash(magnetHref);
            if (string.IsNullOrEmpty(hash))
                continue;

            var title = titleLink.TextContent.Trim();
            var rightCells = row.QuerySelectorAll("td[align='right']").ToList();
            var seeders = rightCells.Count >= 2 && int.TryParse(rightCells[^2].TextContent.Trim(), out var s) ? s : 0;
            var leechers = rightCells.Count >= 1 && int.TryParse(rightCells[^1].TextContent.Trim(), out var l) ? l : 0;

            long sizeBytes;
            DateTime? published;
            if (rightCells.Count >= 3)
            {
                // "Double" layout: a dedicated right-aligned size cell precedes seeders/leechers,
                // and the upload date sits in its own cell elsewhere in the row.
                sizeBytes = ByteSize.Parse(rightCells[^3].TextContent.Trim());
                var dateCell = row.QuerySelectorAll("td").FirstOrDefault(td => UploadDateCellRegex.IsMatch(td.TextContent.Trim()));
                published = dateCell is not null ? ParseUploaded(dateCell.TextContent.Trim()) : null;
            }
            else
            {
                // "Single" layout: date/size/uploader are packed into one descriptive blob.
                var detDesc = row.QuerySelector("font.detDesc")?.TextContent ?? "";
                var descMatch = DetDescRegex.Match(detDesc);
                sizeBytes = descMatch.Success ? ByteSize.Parse(descMatch.Groups[2].Value.Trim()) : 0;
                published = descMatch.Success ? ParseUploaded(descMatch.Groups[1].Value.Trim()) : null;
            }

            results.Add(new TorrentSearchResult
            {
                Title = title,
                InfoHash = hash,
                MagnetUri = Magnet.Build(hash, title),
                SizeBytes = sizeBytes,
                Seeders = seeders,
                Leechers = leechers,
                PublishedAt = published,
                Source = ProviderName
            });
        }
        return results;
    }

    /// <summary>Parses TPB upload dates: "04-25 16:35" (current year), "09-08 2024", "Today 16:35", "Y-day 16:35".</summary>
    private static DateTime? ParseUploaded(string text)
    {
        // The mirror separates fields with &nbsp;, which AngleSharp decodes to U+00A0.
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

    /// <summary>Clamps a peer count into int range so a malformed huge value can't wrap negative.</summary>
    private static int ToInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

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
