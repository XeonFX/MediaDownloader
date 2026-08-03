using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Downloads;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Torrents;

/// <summary>Aggregates all registered torrent providers.</summary>
public class TorrentSearchService
{
    private readonly IReadOnlyList<ITorrentSearchProvider> _providers;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<TorrentSearchService> _logger;

    public TorrentSearchService(IEnumerable<ITorrentSearchProvider> providers,
        IDbContextFactory<AppDbContext> dbFactory, ILogger<TorrentSearchService> logger)
    {
        // The provider set is fixed at DI composition time (assembly-scanned once in
        // ServiceCollectionExtensions.AddTorrentSearch) — materialize once instead of
        // re-allocating a List on every access (Search.razor reads ProviderNames per render).
        _providers = providers.ToList();
        Providers = _providers;
        ProviderNames = _providers.Select(p => p.Name).ToList();
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public IReadOnlyList<ITorrentSearchProvider> Providers { get; }

    public IReadOnlyList<string> ProviderNames { get; }

    /// <param name="provider">Provider name, or null/empty to search all providers.</param>
    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, string? provider = null, CancellationToken ct = default)
    {
        var all = new List<TorrentSearchResult>();
        var gate = new object();
        // Series checks do their own episode/title matching, so don't apply the relevance filter here.
        await SearchStreamAsync(query, provider, batch =>
        {
            lock (gate) all.AddRange(batch);
            return Task.CompletedTask;
        }, filterRelevance: false, ct: ct);

        return SearchResultMerger.Merge(all);
    }

    /// <summary>
    /// Searches providers in parallel, invoking <paramref name="onResults"/> with each provider's
    /// results as soon as that provider finishes. Disabled providers (per settings) are skipped, as
    /// are providers that require an account with no credentials saved yet.
    /// When <paramref name="filterRelevance"/> is true, results whose title doesn't match the query
    /// are dropped. The callbacks may run concurrently for different providers — callers updating
    /// shared state must synchronize.
    ///
    /// Every provider produces a <see cref="ProviderSearchOutcome"/>, delivered to
    /// <paramref name="onProviderDone"/> as it finishes and returned as a set at the end, so a
    /// caller can tell "this site failed" and "this site answered but everything was filtered out"
    /// apart from "there is genuinely nothing to find".
    /// </summary>
    public async Task<IReadOnlyList<ProviderSearchOutcome>> SearchStreamAsync(string query, string? provider,
        Func<IReadOnlyList<TorrentSearchResult>, Task> onResults, bool filterRelevance = true,
        Func<ProviderSearchOutcome, Task>? onProviderDone = null, CancellationToken ct = default)
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
                // Cancellation is the user moving on, not a provider fault — don't report it as one.
                if (ct.IsCancellationRequested)
                    throw;

                _logger.LogWarning(ex, "Search on {Provider} failed", p.Name);
                return await ReportAsync(new ProviderSearchOutcome(
                    p.Name, ProviderSearchStatus.Failed, Error: Describe(ex)));
            }

            var returned = results.Count;
            if (filterRelevance)
                results = results.Where(r => SearchRelevance.Matches(query, r.Title)).ToList();
            var filtered = returned - results.Count;

            if (filtered > 0)
                _logger.LogDebug("{Provider}: {Filtered} of {Returned} results dropped as irrelevant to '{Query}'",
                    p.Name, filtered, returned, query);

            if (results.Count > 0)
                await onResults(results);

            return await ReportAsync(new ProviderSearchOutcome(
                p.Name, ProviderSearchStatus.Ok, returned, filtered));
        });

        return await Task.WhenAll(tasks);

        async Task<ProviderSearchOutcome> ReportAsync(ProviderSearchOutcome outcome)
        {
            if (onProviderDone is not null)
                await onProviderDone(outcome);
            return outcome;
        }
    }

    /// <summary>
    /// A short, user-facing reason a provider failed. Exception messages from the HTTP stack are
    /// long and mention hostnames the user never typed, so the common cases get a plain phrase.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "timed out",
        HttpRequestException { StatusCode: not null } http => $"HTTP {(int)http.StatusCode}",
        HttpRequestException => "unreachable",
        _ => ex.Message
    };

    /// <summary>
    /// Starts a download for a search result, routing through the .torrent-file path for private
    /// trackers and the magnet path for everything else. Resolves the magnet first if the result
    /// hasn't been (e.g. the user clicked Download without opening the info dialog).
    /// </summary>
    public async Task<DownloadItem> StartDownloadAsync(DownloadManager downloads, TorrentSearchResult result,
        int? seriesTaskId = null, string? saveFolder = null, CancellationToken ct = default)
    {
        if (result.NeedsResolution)
            await EnsureDetailsAsync(result, ct);

        if (!string.IsNullOrEmpty(result.TorrentFileUrl))
        {
            var bytes = await GetTorrentFileAsync(result, ct);
            return await downloads.AddDownloadFromTorrentFileAsync(result.Title, bytes, result.Source, seriesTaskId, saveFolder);
        }
        if (string.IsNullOrEmpty(result.MagnetUri))
            throw new InvalidOperationException($"Could not resolve a magnet link for \"{result.Title}\" from {result.Source}.");
        return await downloads.AddDownloadAsync(result.Title, result.MagnetUri, result.Source, seriesTaskId, saveFolder);
    }

    /// <summary>
    /// Lazily fills in a result's magnet link and/or description via the provider's
    /// <see cref="ITorrentDetailsProvider"/>, if it has one and something is still missing.
    /// No-ops (no HTTP request) once the result already has everything the provider can give —
    /// safe to call every time the info dialog opens or a download starts.
    /// </summary>
    public async Task EnsureDetailsAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        if (!result.NeedsResolution && result.Description is not null)
            return;

        if (FindProvider(result.Source) is not ITorrentDetailsProvider detailsProvider)
            return;

        TorrentDetails details;
        try
        {
            details = await detailsProvider.GetDetailsAsync(result, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch details for {Title} from {Source}", result.Title, result.Source);
            return;
        }

        if (!string.IsNullOrEmpty(details.InfoHash))
            result.InfoHash = details.InfoHash;
        if (!string.IsNullOrEmpty(details.MagnetUri))
            result.MagnetUri = details.MagnetUri;
        if (details.Description is not null)
            result.Description = details.Description;
    }

    /// <summary>Fetches the .torrent file for a result whose provider serves files instead of magnets.</summary>
    public async Task<byte[]> GetTorrentFileAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        if (FindProvider(result.Source) is not ITorrentFileSource fileSource)
            throw new InvalidOperationException($"{result.Source} does not serve .torrent files");
        return await fileSource.DownloadTorrentFileAsync(result, ct);
    }

    private ITorrentSearchProvider? FindProvider(string name) =>
        _providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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
