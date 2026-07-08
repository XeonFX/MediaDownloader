using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Series;

public static partial class EpisodeParser
{
    /// <summary>
    /// Tries to extract a (season, episode) pair from a torrent title.
    /// Supports "S01E05", "1x05", "Episode 5", "Ep05", "E05" and anime style "Show - 05".
    /// </summary>
    public static bool TryParse(string title, out int? season, out int episode)
    {
        season = null;
        episode = 0;
        if (string.IsNullOrWhiteSpace(title)) return false;

        var m = SeasonEpisodeRegex().Match(title);
        if (m.Success)
        {
            season = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
            return true;
        }

        m = CrossFormatRegex().Match(title);
        if (m.Success)
        {
            season = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
            return true;
        }

        m = EpisodeWordRegex().Match(title);
        if (m.Success)
        {
            episode = int.Parse(m.Groups[1].Value);
            return true;
        }

        // Anime style: "Show Name - 05 [1080p]". Avoid matching years and resolutions.
        m = AnimeDashRegex().Match(title);
        if (m.Success)
        {
            var value = int.Parse(m.Groups[1].Value);
            if (value is > 0 and < 1900)
            {
                episode = value;
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"[Ss](\d{1,2})[\s._-]*[Ee](\d{1,4})")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\b(\d{1,2})x(\d{2,4})\b")]
    private static partial Regex CrossFormatRegex();

    [GeneratedRegex(@"\b(?:[Ee]p(?:isode)?|[Ee])[\s._]?(\d{1,4})\b")]
    private static partial Regex EpisodeWordRegex();

    [GeneratedRegex(@"[-–]\s*(\d{1,4})(?![\dpPxX])")]
    private static partial Regex AnimeDashRegex();
}
