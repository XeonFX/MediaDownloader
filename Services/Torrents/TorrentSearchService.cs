using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Downloads;
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

    public IReadOnlyList<ITorrentSearchProvider> Providers => _providers.ToList();

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
    /// results as soon as that provider finishes. Disabled providers (per settings) are skipped, as
    /// are providers that require an account with no credentials saved yet.
    /// When <paramref name="filterRelevance"/> is true, results whose title doesn't match the query
    /// are dropped. The callback may run concurrently for different providers — callers updating
    /// shared state must synchronize.
    /// </summary>
    public async Task SearchStreamAsync(string query, string? provider,
        Func<IReadOnlyList<TorrentSearchResult>, Task> onResults, bool filterRelevance = true, CancellationToken ct = default)
    {
        var (disabled, withCredentials) = await GetProviderFiltersAsync(ct);
        var targets = _providers
            .Where(p => string.IsNullOrEmpty(provider) || p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .Where(p => !disabled.Contains(p.Name))
            .Where(p => !p.RequiresCredentials || withCredentials.Contains(p.Name))
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

    /// <summary>
    /// Starts a download for a search result, routing through the .torrent-file path for private
    /// trackers and the magnet path for everything else.
    /// </summary>
    public async Task<DownloadItem> StartDownloadAsync(DownloadManager downloads, TorrentSearchResult result,
        int? seriesTaskId = null, string? saveFolder = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(result.TorrentFileUrl))
        {
            var bytes = await GetTorrentFileAsync(result, ct);
            return await downloads.AddDownloadFromTorrentFileAsync(result.Title, bytes, result.Source, seriesTaskId, saveFolder);
        }
        return await downloads.AddDownloadAsync(result.Title, result.MagnetUri, result.Source, seriesTaskId, saveFolder);
    }

    /// <summary>Fetches the .torrent file for a result whose provider serves files instead of magnets.</summary>
    public async Task<byte[]> GetTorrentFileAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.Name.Equals(result.Source, StringComparison.OrdinalIgnoreCase));
        if (provider is not ITorrentFileSource fileSource)
            throw new InvalidOperationException($"{result.Source} does not serve .torrent files");
        return await fileSource.DownloadTorrentFileAsync(result, ct);
    }

    private async Task<(HashSet<string> Disabled, HashSet<string> WithCredentials)> GetProviderFiltersAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.GetSettingsAsync(ct);
            var withCredentials = (await db.ProviderCredentials.AsNoTracking().ToListAsync(ct))
                .Where(c => c.IsComplete)
                .Select(c => c.ProviderName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (settings.GetDisabledProviders(), withCredentials);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read provider settings; searching all");
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
