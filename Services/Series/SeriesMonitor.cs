using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Torrents;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Series;

/// <summary>
/// Periodically checks torrent sites for new episodes of configured series tasks
/// and queues downloads for them automatically.
/// </summary>
public class SeriesMonitor : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TorrentSearchService _search;
    private readonly DownloadManager _downloads;
    private readonly ILogger<SeriesMonitor> _logger;

    /// <summary>Raised after a check pass so the UI can refresh.</summary>
    public event Action? SeriesChanged;

    public SeriesMonitor(
        IDbContextFactory<AppDbContext> dbFactory,
        TorrentSearchService search,
        DownloadManager downloads,
        ILogger<SeriesMonitor> logger)
    {
        _dbFactory = dbFactory;
        _search = search;
        _downloads = downloads;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the download manager a moment to finish resuming.
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckDueTasksAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Series check pass failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task CheckDueTasksAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var due = await db.SeriesTasks
            .Where(t => t.Enabled)
            .ToListAsync(ct);

        foreach (var task in due.Where(t =>
                     t.LastCheckedAt == null ||
                     t.LastCheckedAt.Value.AddMinutes(t.CheckIntervalMinutes) <= now))
        {
            // Isolate each task: one throwing (e.g. a provider bug) must not stop every other
            // due task in this pass from being checked, or their LastCheckedAt from updating.
            try
            {
                await CheckTaskAsync(db, task, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Series check failed for '{Name}'", task.Name);
            }
        }
    }

    /// <summary>Runs a single check for one task immediately (also used by the "Check now" button).</summary>
    public async Task CheckTaskNowAsync(int taskId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var task = await db.SeriesTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is not null)
            await CheckTaskAsync(db, task, ct);
    }

    private async Task CheckTaskAsync(AppDbContext db, SeriesTask task, CancellationToken ct)
    {
        // Never auto-download for a task without a real search query — an empty query
        // matches arbitrary torrents. Just stamp the check time and bail.
        if (string.IsNullOrWhiteSpace(task.Query))
        {
            task.LastCheckedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogInformation("Checking series '{Name}' for episode {Episode}", task.Name, task.NextEpisode);

        // Catch up on multiple episodes in one pass, but cap the work per check.
        for (var guard = 0; guard < 25; guard++)
        {
            if (task.IsFinished)
            {
                task.Enabled = false;
                break;
            }

            var episode = task.NextEpisode;
            var result = await FindEpisodeAsync(task, episode, ct);
            if (result is null)
                break;

            await _search.StartDownloadAsync(_downloads, result, task.Id, task.DownloadFolder);
            task.LastDownloadedEpisode = episode;
            _logger.LogInformation("Series '{Name}': queued episode {Episode} ({Title})", task.Name, episode, result.Title);
        }

        if (task.IsFinished)
            task.Enabled = false;

        task.LastCheckedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        SeriesChanged?.Invoke();
    }

    private async Task<TorrentSearchResult?> FindEpisodeAsync(SeriesTask task, int episode, CancellationToken ct)
    {
        // Try a targeted query first, then fall back to the plain query.
        var queries = new List<string>();
        if (task.Season.HasValue)
            queries.Add($"{task.Query} S{task.Season:00}E{episode:00}");
        queries.Add($"{task.Query} {episode:00}");
        queries.Add(task.Query);

        foreach (var query in queries.Distinct())
        {
            var results = await _search.SearchAsync(query, task.Provider, ct);
            var match = results
                .Where(r => MatchesTask(r, task, episode))
                .OrderByDescending(r => r.Seeders)
                .FirstOrDefault();
            if (match is not null)
                return match;
        }
        return null;
    }

    private static bool MatchesTask(TorrentSearchResult result, SeriesTask task, int wantedEpisode)
    {
        var title = result.Title;

        // Every word of the query and the optional filter must appear in the title.
        var requiredTokens = task.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat((task.TitleFilter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!requiredTokens.All(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!EpisodeParser.TryParse(title, out var season, out var episode))
            return false;

        if (episode != wantedEpisode)
            return false;

        if (task.Season.HasValue && season.HasValue && season.Value != task.Season.Value)
            return false;
        if (task.Season.HasValue && !season.HasValue)
            return false; // season required but the title has no season marker

        return true;
    }
}
