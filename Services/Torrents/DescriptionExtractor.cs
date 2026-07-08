using System.Net;
using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>Strips a scraped HTML description block down to safe, readable plain text.</summary>
public static class DescriptionExtractor
{
    private static readonly Regex BlockBreaks = new(@"<(br|/p|/div|/li)\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static string? ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var withBreaks = BlockBreaks.Replace(html, "\n");
        var stripped = Tags.Replace(withBreaks, "");
        var decoded = WebUtility.HtmlDecode(stripped);
        var collapsed = BlankLines.Replace(decoded, "\n\n").Trim();
        return string.IsNullOrWhiteSpace(collapsed) ? null : collapsed;
    }
}
