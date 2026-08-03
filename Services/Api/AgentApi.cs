using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Series;
using MediaDownloader.Services.Torrents;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Api;

/// <summary>
/// Everything an agent can do, in one place. The REST endpoints and the MCP tools are both thin
/// wrappers over this — neither holds logic of its own, so the two surfaces cannot drift apart or
/// enforce different rules.
///
/// Deliberately not exposed here: any settings *write*. An agent has no business changing an SMTP
/// password or the Telegram token, so <see cref="GetSettingsAsync"/> is read-only and there is no
/// counterpart.
/// </summary>
public class AgentApi
{
    /// <summary>Default rows returned by a search when the caller doesn't say.</summary>
    private const int DefaultSearchLimit = 25;
    private const int MaxSearchLimit = 200;

    private readonly TorrentSearchService _search;
    private readonly DownloadManager _downloads;
    private readonly SeriesTaskService _series;
    private readonly SeriesMonitor _seriesMonitor;
    private readonly SearchResultCache _cache;
    private readonly AgentRateLimiter _rateLimiter;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AgentApi> _logger;

    public AgentApi(
        TorrentSearchService search,
        DownloadManager downloads,
        SeriesTaskService series,
        SeriesMonitor seriesMonitor,
        SearchResultCache cache,
        AgentRateLimiter rateLimiter,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AgentApi> logger)
    {
        _search = search;
        _downloads = downloads;
        _series = series;
        _seriesMonitor = seriesMonitor;
        _cache = cache;
        _rateLimiter = rateLimiter;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // ---- Sources & search ----

    public async Task<IReadOnlyList<SourceDto>> GetSourcesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await db.GetSettingsAsync(ct);
        var withCredentials = (await db.ProviderCredentials.AsNoTracking().ToListAsync(ct))
            .Where(c => c.IsComplete)
            .Select(c => c.ProviderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _search.Providers
            .Select(p => new SourceDto(
                p.Name,
                settings.IsProviderEnabled(p.Name),
                NeedsCredentials: p.RequiresCredentials && !withCredentials.Contains(p.Name)))
            .ToList();
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new AgentApiException("A search query is required.");

        _rateLimiter.EnsureAllowed("search");
        await RequireAvailableSourceAsync(request.Source, ct);

        var limit = Math.Clamp(request.Limit ?? DefaultSearchLimit, 1, MaxSearchLimit);

        var collected = new List<TorrentSearchResult>();
        var gate = new object();
        var outcomes = await _search.SearchStreamAsync(request.Query, request.Source,
            batch =>
            {
                lock (gate) collected.AddRange(batch);
                return Task.CompletedTask;
            }, ct: ct);

        // Same de-duplication the UI applies, so an agent doesn't see one torrent three times just
        // because three sources indexed it.
        var deduped = SearchResultMerger.Merge(collected);
        var page = deduped.Take(limit).ToList();

        _logger.LogInformation("Agent search '{Query}': {Count} of {Total} results from {Sources} sources",
            request.Query, page.Count, deduped.Count, outcomes.Count);

        // Report the total and flag truncation explicitly. The per-source counts below add up to
        // everything the sites returned, so without this an agent sees "173 returned, 1 filtered"
        // next to two results and has no way to tell a limit from a bug.
        return new SearchResponse(
            page.Select(r => ToDto(r, _cache.Add(r))).ToList(),
            outcomes.Select(o => new SourceOutcomeDto(
                o.Provider,
                o.Status == ProviderSearchStatus.Ok ? "ok" : "failed",
                o.Returned, o.Filtered, o.Error)).ToList(),
            TotalMatched: deduped.Count,
            Truncated: deduped.Count > page.Count);
    }

    public async Task<TorrentDetailsDto> GetTorrentDetailsAsync(string resultId, CancellationToken ct = default)
    {
        _rateLimiter.EnsureAllowed("detail");
        var result = _cache.Get(resultId);
        await _search.EnsureDetailsAsync(result, ct);
        return new TorrentDetailsDto(
            ToDto(result, resultId),
            result.Description,
            string.IsNullOrEmpty(result.MagnetUri) ? null : result.MagnetUri);
    }

    // ---- Downloads ----

    public async Task<DownloadDto> StartDownloadAsync(StartDownloadRequest request, CancellationToken ct = default)
    {
        var folder = await ResolveSaveFolderAsync(request.Folder, ct);

        if (!string.IsNullOrWhiteSpace(request.ResultId))
        {
            var result = _cache.Get(request.ResultId);
            var item = await _search.StartDownloadAsync(_downloads, result, saveFolder: folder, ct: ct);
            _logger.LogInformation("Agent started download '{Name}' from {Source}", item.Name, item.Source);
            return DownloadDto.From(item);
        }

        if (!string.IsNullOrWhiteSpace(request.Magnet))
        {
            if (!request.Magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                throw new AgentApiException("`magnet` must be a magnet: URI.");

            DownloadItem item;
            try
            {
                item = await _downloads.AddDownloadAsync(name: "", request.Magnet, source: "Agent",
                    saveFolder: folder);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new AgentApiException($"That magnet link could not be parsed: {ex.Message}");
            }
            _logger.LogInformation("Agent started download from a supplied magnet: {Name}", item.Name);
            return DownloadDto.From(item);
        }

        throw new AgentApiException(
            "Provide either `resultId` (from a search) or `magnet`. For sources that resolve " +
            "magnets lazily, and for private trackers, only `resultId` works.");
    }

    public IReadOnlyList<DownloadDto> GetDownloads(string? status = null)
    {
        var items = _downloads.GetDownloads().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<DownloadStatus>(status, ignoreCase: true, out var wanted))
                throw new AgentApiException(
                    $"Unknown status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<DownloadStatus>())}.");
            items = items.Where(i => i.Status == wanted);
        }

        return items.Select(DownloadDto.From).ToList();
    }

    public DownloadDto GetDownload(int id) =>
        DownloadDto.From(_downloads.GetDownloads().FirstOrDefault(d => d.Id == id)
                         ?? throw AgentApiException.NotFound($"No download with id {id}."));

    public async Task<DownloadDto> PauseDownloadAsync(int id)
    {
        RequireDownload(id);
        await _downloads.PauseAsync(id);
        return GetDownload(id);
    }

    public async Task<DownloadDto> ResumeDownloadAsync(int id)
    {
        RequireDownload(id);
        await _downloads.ResumeAsync(id);
        return GetDownload(id);
    }

    /// <summary>
    /// Removes a download. <paramref name="deleteFiles"/> also erases what was downloaded — the one
    /// irreversible thing an agent can do here, so it defaults to false everywhere it is exposed.
    /// </summary>
    public async Task DeleteDownloadAsync(int id, bool deleteFiles = false)
    {
        var item = RequireDownload(id);
        _logger.LogInformation("Agent deleting download '{Name}' (deleteFiles: {DeleteFiles})", item.Name, deleteFiles);
        await _downloads.DeleteAsync(id, deleteFiles);
    }

    private DownloadItem RequireDownload(int id) =>
        _downloads.GetDownloads().FirstOrDefault(d => d.Id == id)
        ?? throw AgentApiException.NotFound($"No download with id {id}.");

    // ---- Series tasks ----

    public async Task<IReadOnlyList<SeriesTaskDto>> GetSeriesTasksAsync(CancellationToken ct = default) =>
        (await _series.GetAllAsync(ct)).Select(SeriesTaskDto.From).ToList();

    public async Task<SeriesTaskDto> GetSeriesTaskAsync(int id, CancellationToken ct = default) =>
        SeriesTaskDto.From(await RequireSeriesTaskAsync(id, ct));

    public async Task<SeriesTaskDto> CreateSeriesTaskAsync(SeriesTaskRequest request, CancellationToken ct = default)
    {
        Validate(request);
        await RequireAvailableSourceAsync(request.Provider, ct);
        var task = new SeriesTask();
        Apply(request, task, await ResolveSaveFolderAsync(request.DownloadFolder, ct));
        return SeriesTaskDto.From(await _series.AddAsync(task, ct));
    }

    /// <summary>
    /// Replaces every field of a task. The request type requires all of them, so a caller cannot
    /// half-replace a rule by accident; use <see cref="PatchSeriesTaskAsync"/> to change a subset.
    /// This is the only way to clear a field, since a patch reads null as "leave alone".
    /// </summary>
    public async Task<SeriesTaskDto> UpdateSeriesTaskAsync(int id, SeriesTaskReplacement replacement, CancellationToken ct = default)
    {
        var request = replacement.ToRequest();
        Validate(request);
        await RequireAvailableSourceAsync(request.Provider, ct);
        var task = await RequireSeriesTaskAsync(id, ct);
        Apply(request, task, await ResolveSaveFolderAsync(request.DownloadFolder, ct));
        await _series.SaveAsync(task, ct);
        return SeriesTaskDto.From(task);
    }

    /// <summary>
    /// Changes only the fields the caller actually supplied; anything left null keeps its current
    /// value.
    ///
    /// The full-replace form is a trap for an agent, which routinely omits optional arguments when
    /// it means to change one thing. Omitting them there silently reset season, start episode and
    /// check interval to their defaults and flipped <c>Enabled</c> back to true — re-arming a task
    /// the user had deliberately switched off and sending it back to episode 1, which then
    /// re-downloads everything it had already fetched.
    /// </summary>
    public async Task<SeriesTaskDto> PatchSeriesTaskAsync(int id, SeriesTaskPatch patch, CancellationToken ct = default)
    {
        var task = await RequireSeriesTaskAsync(id, ct);

        // Fold the patch onto the task's current values, then validate the *result* — so a patch
        // can't leave the task in a state a create would have rejected.
        var merged = new SeriesTaskRequest(
            patch.Name ?? task.Name,
            patch.Query ?? task.Query,
            patch.Provider ?? task.Provider,
            patch.TitleFilter ?? task.TitleFilter,
            patch.Season ?? task.Season,
            patch.StartEpisode ?? task.StartEpisode,
            patch.EndEpisode ?? task.EndEpisode,
            patch.CheckIntervalMinutes ?? task.CheckIntervalMinutes,
            patch.Enabled ?? task.Enabled,
            patch.DownloadFolder ?? task.DownloadFolder);

        Validate(merged);
        if (patch.Provider is not null)
            await RequireAvailableSourceAsync(merged.Provider, ct);

        // Only re-check the folder when the patch actually names one: an existing task may legally
        // point somewhere the user chose in the UI, and simply editing its name must not fail.
        var folder = patch.DownloadFolder is null
            ? task.DownloadFolder
            : await ResolveSaveFolderAsync(patch.DownloadFolder, ct);

        Apply(merged, task, folder);
        await _series.SaveAsync(task, ct);
        return SeriesTaskDto.From(task);
    }

    public async Task DeleteSeriesTaskAsync(int id, CancellationToken ct = default) =>
        await _series.DeleteAsync(await RequireSeriesTaskAsync(id, ct), ct);

    /// <summary>
    /// Runs a series check immediately instead of waiting for its interval. This can queue
    /// downloads, so it is a write even though it reads like a refresh.
    /// </summary>
    public async Task<SeriesTaskDto> CheckSeriesTaskNowAsync(int id, CancellationToken ct = default)
    {
        _rateLimiter.EnsureAllowed("series check");
        var task = await RequireSeriesTaskAsync(id, ct);
        await RequireAvailableSourceAsync(task.Provider, ct);
        await _seriesMonitor.CheckTaskNowAsync(id, ct);
        return SeriesTaskDto.From(await RequireSeriesTaskAsync(id, ct));
    }

    private async Task<SeriesTask> RequireSeriesTaskAsync(int id, CancellationToken ct)
    {
        var tasks = await _series.GetAllAsync(ct);
        return tasks.FirstOrDefault(t => t.Id == id)
               ?? throw AgentApiException.NotFound($"No series task with id {id}.");
    }

    private static void Validate(SeriesTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new AgentApiException("A series task needs a name.");
        // SeriesMonitor refuses to auto-download for a blank query (it would match anything), so
        // rejecting it here turns a silently inert task into an obvious error at creation time.
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new AgentApiException("A series task needs a search query, otherwise it can never match an episode.");
        if (request.StartEpisode < 1)
            throw new AgentApiException("startEpisode must be 1 or greater.");
        if (request.EndEpisode is { } end && end < request.StartEpisode)
            throw new AgentApiException("endEpisode cannot be before startEpisode.");
        if (request.CheckIntervalMinutes < 1)
            throw new AgentApiException("checkIntervalMinutes must be 1 or greater.");
    }

    private static void Apply(SeriesTaskRequest request, SeriesTask task, string? resolvedFolder)
    {
        task.Name = request.Name;
        task.Query = request.Query;
        task.Provider = request.Provider;
        task.TitleFilter = request.TitleFilter;
        task.Season = request.Season;
        task.StartEpisode = request.StartEpisode;
        task.EndEpisode = request.EndEpisode;
        task.CheckIntervalMinutes = request.CheckIntervalMinutes;
        task.Enabled = request.Enabled;
        task.DownloadFolder = resolvedFolder;
    }

    /// <summary>
    /// Confines an agent-chosen save folder to the download root — see <see cref="SaveFolderPolicy"/>
    /// for why that boundary exists. Returns null when no folder was asked for, leaving the caller's
    /// own default in play.
    /// </summary>
    private async Task<string?> ResolveSaveFolderAsync(string? requested, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await db.GetSettingsAsync(ct);
        return SaveFolderPolicy.Resolve(requested, settings.DownloadFolder);
    }

    /// <summary>
    /// Rejects a named source that the search aggregator would otherwise silently skip. When no
    /// source is named, also reject an installation with no usable sources instead of returning an
    /// ambiguous empty result/outcome set.
    /// </summary>
    private async Task RequireAvailableSourceAsync(string? requested, CancellationToken ct)
    {
        var sources = await GetSourcesAsync(ct);
        if (string.IsNullOrWhiteSpace(requested))
        {
            if (sources.Any(s => s.Enabled && !s.NeedsCredentials))
                return;

            throw new AgentApiException(
                "No torrent sources are available. Enable a source in Settings and configure any required credentials.");
        }

        var source = sources.FirstOrDefault(s => s.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (source is null)
            throw new AgentApiException(
                $"Unknown source '{requested}'. Valid sources: {string.Join(", ", sources.Select(s => s.Name))}.");
        if (!source.Enabled)
            throw new AgentApiException($"Source '{source.Name}' is disabled. Enable it in Settings before using it.");
        if (source.NeedsCredentials)
            throw new AgentApiException(
                $"Source '{source.Name}' needs configured credentials. Add them in Settings before using it.");
    }

    // ---- Settings (read-only) ----

    public async Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await db.GetSettingsAsync(ct);
        return new SettingsDto(settings.DownloadFolder, settings.PostDownloadAction.ToString());
    }

    private static SearchResultDto ToDto(TorrentSearchResult r, string resultId) => new(
        resultId, r.Title, r.Source, r.SizeBytes, r.SizeDisplay, r.Seeders, r.Leechers,
        r.PublishedAt, r.DetailsUrl);
}
