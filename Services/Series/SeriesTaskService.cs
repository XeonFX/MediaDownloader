using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Series;

/// <summary>
/// Data-access layer for series tasks, extracted out of Series.razor so the page focuses on
/// presentation and this logic can be exercised without a full component render.
/// </summary>
public class SeriesTaskService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public SeriesTaskService(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<SeriesTask>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SeriesTasks.OrderBy(t => t.Id).ToListAsync(ct);
    }

    public async Task<SeriesTask> AddAsync(SeriesTask task, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SeriesTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task SaveAsync(SeriesTask task, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SeriesTasks.Update(task);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(SeriesTask task, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.SeriesTasks.Remove(task);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The task's own download folder if set, otherwise the global default from Settings.</summary>
    public async Task<string> GetEffectiveDownloadFolderAsync(SeriesTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(task.DownloadFolder))
            return task.DownloadFolder;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await db.GetSettingsAsync(ct)).DownloadFolder;
    }
}
