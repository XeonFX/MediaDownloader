using System.Collections.Concurrent;
using System.Security.Cryptography;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Services.Api;

/// <summary>
/// Hands out short-lived ids for search results and gives the objects back later.
///
/// Starting a download needs the <see cref="TorrentSearchResult"/> itself, not just its magnet:
/// 1337x rows carry a placeholder hash until their detail page is fetched, and PTE serves a
/// .torrent file rather than a magnet, so <see cref="TorrentSearchService.StartDownloadAsync"/>
/// resolves both from the live object. The Blazor UI simply keeps that object in the circuit's
/// SearchState; an agent calling over HTTP has no such continuity, so it gets an id instead and
/// this holds the object in between.
///
/// Entries expire on a sliding window and the cache is bounded, so a long-running app that has
/// served thousands of searches doesn't accumulate them. An id that has aged out is reported as
/// such ("search again") rather than failing obscurely.
/// </summary>
public class SearchResultCache
{
    /// <summary>How long an unused id stays valid. Long enough for an agent to think, short enough that stale seeder counts don't get acted on.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Hard cap on retained results. Reached only by an agent searching in a tight loop; the oldest
    /// entries are dropped first, which is also the order they would have expired in.
    /// </summary>
    private const int MaxEntries = 2_000;

    private sealed record Entry(TorrentSearchResult Result, long SequenceNumber)
    {
        public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _sequence;

    public SearchResultCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Stores a result and returns the id an agent uses to refer to it.</summary>
    public string Add(TorrentSearchResult result)
    {
        Prune();

        var id = "r_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _entries[id] = new Entry(result, Interlocked.Increment(ref _sequence)) { LastUsedAt = UtcNow };
        return id;
    }

    /// <summary>
    /// Looks up a result, refreshing its expiry. Throws <see cref="AgentApiException"/> when the id
    /// is unknown or has expired — both mean the same thing to a caller, so they get the same
    /// actionable message.
    /// </summary>
    public TorrentSearchResult Get(string resultId)
    {
        if (!string.IsNullOrEmpty(resultId) && _entries.TryGetValue(resultId, out var entry)
            && UtcNow - entry.LastUsedAt <= Lifetime)
        {
            entry.LastUsedAt = UtcNow;
            return entry.Result;
        }

        _entries.TryRemove(resultId ?? "", out _);
        throw AgentApiException.NotFound(
            $"Search result '{resultId}' is unknown or has expired (results are kept for " +
            $"{Lifetime.TotalMinutes:0} minutes). Run the search again to get fresh result ids.");
    }

    /// <summary>Current entry count. Exposed for tests.</summary>
    internal int Count => _entries.Count;

    /// <summary>Drops expired entries, then the oldest ones if the cache is still over its cap.</summary>
    private void Prune()
    {
        var now = UtcNow;
        foreach (var (id, entry) in _entries)
        {
            if (now - entry.LastUsedAt > Lifetime)
                _entries.TryRemove(id, out _);
        }

        if (_entries.Count < MaxEntries)
            return;

        // Insertion order, not last-used: cheap, stable, and close enough — an id being actively
        // used is very unlikely to be among the oldest few hundred of two thousand.
        foreach (var (id, _) in _entries
                     .OrderBy(e => e.Value.SequenceNumber)
                     .Take(_entries.Count - MaxEntries + 1))
        {
            _entries.TryRemove(id, out _);
        }
    }
}
