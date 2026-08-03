namespace MediaDownloader.Services.Torrents;

/// <summary>
/// De-duplicates search results coming from overlapping providers (Torrents-CSV also indexes The
/// Pirate Bay, TheRARBG and Nyaa torrents show up in aggregators, …), keeping the healthiest row
/// per torrent.
///
/// Two callers need this — <see cref="TorrentSearchService.SearchAsync"/>, which merges one
/// complete result set, and the Search page, which merges each provider's batch into a list as it
/// streams in. They used to carry separate copies of the rule, with a comment in each saying it
/// "mirrors" the other; both now go through here so they can't drift apart.
/// </summary>
public static class SearchResultMerger
{
    /// <summary>
    /// Merges a complete result set: one row per info hash (the highest-seeded), hash-less rows
    /// passed through untouched, sorted by seeders descending.
    /// </summary>
    public static List<TorrentSearchResult> Merge(IEnumerable<TorrentSearchResult> results)
    {
        var all = results.ToList();

        // Only results that carry an info hash can be deduped. A result still awaiting detail
        // resolution has a blank hash, and grouping those together would collapse unrelated
        // torrents into one, dropping all but the highest-seeded — so they pass through untouched.
        var deduped = all
            .Where(r => !string.IsNullOrEmpty(r.InfoHash))
            .GroupBy(r => r.InfoHash, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(r => r.Seeders)!);

        return all
            .Where(r => string.IsNullOrEmpty(r.InfoHash))
            .Concat(deduped)
            .OrderByDescending(r => r.Seeders)
            .ToList();
    }

    /// <summary>
    /// Merges one incoming batch into an already-sorted list, in place — the streaming counterpart
    /// of <see cref="Merge"/>, for callers that show results as each provider finishes.
    /// <paramref name="byHash"/> carries the dedup index across calls and must belong to
    /// <paramref name="target"/>; create it with <see cref="CreateIndex"/>.
    /// </summary>
    public static void MergeInto(List<TorrentSearchResult> target,
        Dictionary<string, TorrentSearchResult> byHash, IEnumerable<TorrentSearchResult> batch)
    {
        foreach (var result in batch)
        {
            if (!string.IsNullOrEmpty(result.InfoHash))
            {
                if (byHash.TryGetValue(result.InfoHash, out var existing))
                {
                    // Prefer whichever provider reports more seeders rather than whichever streamed
                    // in first — an aggregator's cached count can lag the source site's live number.
                    if (result.Seeders <= existing.Seeders) continue;
                    target.Remove(existing);
                }
                byHash[result.InfoHash] = result;
            }
            target.Add(result);
        }
        target.Sort((a, b) => b.Seeders.CompareTo(a.Seeders));
    }

    /// <summary>Creates the dedup index <see cref="MergeInto"/> threads through its calls.</summary>
    public static Dictionary<string, TorrentSearchResult> CreateIndex() =>
        new(StringComparer.OrdinalIgnoreCase);
}
