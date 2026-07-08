using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>Helpers for building and parsing BitTorrent magnet links.</summary>
public static class Magnet
{
    /// <summary>
    /// A broad set of well-known public trackers. Providers rebuild magnets with these rather than
    /// trusting the tracker list served by a (possibly untrusted) mirror.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTrackers = new[]
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.dler.org:6969/announce"
    };

    private static readonly Regex InfoHashRegex =
        new(@"xt=urn:btih:([0-9A-Za-z]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Builds a magnet URI from an info hash and display name using the given trackers (defaults to <see cref="DefaultTrackers"/>).</summary>
    public static string Build(string infoHash, string name, IReadOnlyList<string>? trackers = null)
    {
        var tr = string.Join("", (trackers ?? DefaultTrackers).Select(t => "&tr=" + Uri.EscapeDataString(t)));
        return $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(name)}{tr}";
    }

    /// <summary>Extracts the info hash (btih) from a magnet URI, or null if none is present.</summary>
    public static string? ExtractInfoHash(string magnetUri)
    {
        var m = InfoHashRegex.Match(magnetUri);
        return m.Success ? m.Groups[1].Value : null;
    }
}
