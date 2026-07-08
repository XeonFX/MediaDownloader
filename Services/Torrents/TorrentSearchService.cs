using MediaDownloader.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Torrents;

/// <summary>Aggregates all registered torrent providers.</summary>
public class TorrentSearchService
{
    private readonly IEnumerable<ITorrentSearchProvider> _providers;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<TorrentSearchService> _logger;

    public TorrentSearchService(IEnumerable<ITorrentSearchProvider> providers,
        IDbContextFactory<AppDbContext> dbFactory, ILogger<TorrentSearchService> logger)
    {
        _providers = providers;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToList();

    /// <param name="provider">Provider name, or null/empty to search all providers.</param>
    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, string? provider = null, CancellationToken ct = default)
    {
        var all = new List<TorrentSearchResult>();
        // Series checks do their own episode/title matching, so don't apply the relevance filter here.
        await SearchStreamAsync(query, provider, batch =>
        {
            lock (all) all.AddRange(batch);
            return Task.CompletedTask;
        }, filterRelevance: false, ct);
        // Providers overlap (e.g. Torrents-CSV also indexes The Pirate Bay) — keep one row per torrent.
        return all
            .GroupBy(r => r.InfoHash.ToUpperInvariant())
            .Select(g => g.MaxBy(r => r.Seeders)!)
            .OrderByDescending(r => r.Seeders)
            .ToList();
    }

    /// <summary>
    /// Searches providers in parallel, invoking <paramref name="onResults"/> with each provider's
    /// results as soon as that provider finishes. Disabled providers (per settings) are skipped.
    /// When <paramref name="filterRelevance"/> is true, results whose title doesn't match the query
    /// are dropped. The callback may run concurrently for different providers — callers updating
    /// shared state must synchronize.
    /// </summary>
    public async Task SearchStreamAsync(string query, string? provider,
        Func<IReadOnlyList<TorrentSearchResult>, Task> onResults, bool filterRelevance = true, CancellationToken ct = default)
    {
        var disabled = await GetDisabledProvidersAsync(ct);
        var targets = _providers
            .Where(p => string.IsNullOrEmpty(provider) || p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .Where(p => !disabled.Contains(p.Name))
            .ToList();

        var tasks = targets.Select(async p =>
        {
            IReadOnlyList<TorrentSearchResult> results;
            try
            {
                results = await p.SearchAsync(query, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Search on {Provider} failed", p.Name);
                return;
            }

            if (filterRelevance)
                results = results.Where(r => SearchRelevance.Matches(query, r.Title)).ToList();

            if (results.Count > 0)
                await onResults(results);
        });

        await Task.WhenAll(tasks);
    }

    private async Task<HashSet<string>> GetDisabledProvidersAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.GetSettingsAsync(ct);
            return settings.GetDisabledProviders();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read disabled providers; searching all");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
