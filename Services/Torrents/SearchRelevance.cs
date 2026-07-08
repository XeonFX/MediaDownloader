using System.Text.RegularExpressions;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Filters out results a provider returned that don't actually match the query. Some sites (notably
/// 1337x) match query terms loosely and, when sorted by seeders, float unrelated high-seed torrents
/// to the top — so we require every meaningful query term to appear in the result title.
/// </summary>
public static class SearchRelevance
{
    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static bool Matches(string query, string title)
    {
        var tokens = Tokenize(query);
        if (tokens.Length == 0)
            return true; // nothing meaningful to match on — don't filter

        var normalizedTitle = Normalize(title);
        return tokens.All(normalizedTitle.Contains);
    }

    private static string Normalize(string text) =>
        NonAlphanumeric.Replace(text.ToLowerInvariant(), " ").Trim();

    private static string[] Tokenize(string text) =>
        Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToArray();
}
