using System.Collections.Concurrent;
using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using MonoTorrent;
using MonoTorrent.Client;

namespace MediaDownloader.Services.Downloads;

/// <summary>
/// Central torrent engine built on MonoTorrent. Persists every download to the database
/// and resumes all unfinished downloads when the application starts.
/// </summary>
public class DownloadManager : IHostedService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly NotificationDispatcher _notifications;
    private readonly ILogger<DownloadManager> _logger;

    /// <summary>
    /// How long a download may sit fetching magnet metadata before we give up and mark it failed.
    /// A dead torrent (no seeders) otherwise shows "Fetching metadata" forever with no feedback.
    /// </summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(3);

    private ClientEngine? _engine;
    private readonly ConcurrentDictionary<int, TorrentManager> _managers = new();
    private readonly ConcurrentDictionary<int, DownloadItem> _items = new();
    private readonly ConcurrentDictionary<int, byte> _removing = new();
    // When each download first entered the metadata-fetching state, used to enforce MetadataTimeout.
    private readonly ConcurrentDictionary<int, DateTime> _metadataSince = new();
    // Downloads we've failed for a metadata timeout; the state handler ignores their stop transition.
    private readonly ConcurrentDictionary<int, byte> _metadataFailed = new();
    private Timer? _timer;
    private int _ticks;
    // Set during shutdown so the stop-induced state changes don't overwrite each download's live
    // status (Downloading/FetchingMetadata) with Paused — otherwise they wouldn't auto-resume on restart.
    private volatile bool _shuttingDown;

    /// <summary>Raised whenever download progress/state changes; UI pages subscribe to refresh.</summary>
    public event Action? DownloadsChanged;

    public DownloadManager(
        IDbContextFactory<AppDbContext> dbFactory,
        NotificationDispatcher notifications,
        ILogger<DownloadManager> logger)
    {
        _dbFactory = dbFactory;
        _notifications = notifications;
        _logger = logger;
    }

    public IReadOnlyList<DownloadItem> GetDownloads() =>
        _items.Values.OrderByDescending(i => i.AddedAt).ToList();

    public async Task StartAsync(CancellationToken ct)
    {
        var settings = new EngineSettingsBuilder
        {
            AutoSaveLoadFastResume = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = AppPaths.TorrentCacheDirectory,
            // On networks that filter P2P traffic most outbound peer connections fail. Raising the
            // half-open limit lets the engine churn through unreachable peers faster to reach the ones
            // that do connect, which is what a magnet needs to fetch its metadata. Ports are left at
            // the OS-assigned default: a fixed port risks a hard bind failure if another client (or a
            // not-yet-released previous instance) holds it, which silently kills peer discovery.
            MaximumHalfOpenConnections = 20
        };
        _engine = new ClientEngine(settings.ToSettings());

        // Resume everything from the database.
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var all = await db.Downloads.AsNoTracking().ToListAsync(ct);
            foreach (var item in all)
            {
                if (item.Progress >= 100 && item.Status != DownloadStatus.Completed)
                {
                    item.Status = DownloadStatus.Completed;
                    item.CompletedAt ??= DateTime.UtcNow;
                }
                _items[item.Id] = item;
            }
            await db.Downloads
                .Where(d => d.Progress >= 100 && d.Status != DownloadStatus.Completed)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DownloadStatus.Completed), ct);
        }

        foreach (var item in _items.Values.Where(i => i.Status != DownloadStatus.Completed && i.Status != DownloadStatus.Error))
        {
            try
            {
                await AttachAndStartAsync(item, startPaused: item.Status == DownloadStatus.Paused);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume {Name}", item.Name);
                item.Status = DownloadStatus.Error;
                item.Error = ex.Message;
                await PersistAsync(item);
            }
        }

        _timer = new Timer(_ => OnTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _logger.LogInformation("Download manager started, resumed {Count} downloads", _managers.Count);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _shuttingDown = true;
        _timer?.Dispose();
        await SaveProgressToDbAsync();
        if (_engine is not null)
        {
            await _engine.StopAllAsync();
            _engine.Dispose();
        }
    }

    public async Task<DownloadItem> AddDownloadAsync(string name, string magnetUri, string source,
        int? seriesTaskId = null, string? saveFolder = null)
    {
        var magnet = MagnetLink.Parse(magnetUri);
        var hash = magnet.InfoHashes.V1OrV2.ToHex();

        var existing = _items.Values.FirstOrDefault(i => i.InfoHash.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var settings = await db.GetSettingsAsync();
        var savePath = string.IsNullOrWhiteSpace(saveFolder) ? settings.DownloadFolder : saveFolder;
        Directory.CreateDirectory(savePath);

        var item = new DownloadItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? magnet.Name ?? hash : name,
            MagnetUri = magnetUri,
            InfoHash = hash,
            SavePath = savePath,
            Source = source,
            Status = DownloadStatus.Queued,
            SeriesTaskId = seriesTaskId
        };
        db.Downloads.Add(item);
        await db.SaveChangesAsync();
        _items[item.Id] = item;

        await AttachAndStartAsync(item, startPaused: false);
        DownloadsChanged?.Invoke();
        return item;
    }

    private async Task AttachAndStartAsync(DownloadItem item, bool startPaused)
    {
        if (_engine is null) throw new InvalidOperationException("Engine not started");

        var manager = await _engine.AddAsync(MagnetLink.Parse(item.MagnetUri), item.SavePath);
        _managers[item.Id] = manager;

        manager.TorrentStateChanged += async (_, e) =>
        {
            try { await OnTorrentStateChangedAsync(item, manager, e); }
            catch (Exception ex) { _logger.LogError(ex, "State change handling failed for {Name}", item.Name); }
        };

        if (!startPaused)
        {
            await manager.StartAsync();
            item.Status = DownloadStatus.FetchingMetadata;

            if (!item.StartNotificationSent)
            {
                item.StartNotificationSent = true;
                await PersistAsync(item);
                _ = _notifications.DispatchAsync(new NotificationEvent(
                    NotificationKind.DownloadStarted, "Download started", item.Name));
            }
        }
    }

    private async Task OnTorrentStateChangedAsync(DownloadItem item, TorrentManager manager, TorrentStateChangedEventArgs e)
    {
        // Ignore state changes for a download that is being deleted, one we've already failed for a
        // metadata timeout (its own StopAsync would otherwise flip the status back to Paused), or any
        // download while the app is shutting down (so it keeps its live status and resumes next launch).
        if (_shuttingDown || _removing.ContainsKey(item.Id) || _metadataFailed.ContainsKey(item.Id))
            return;

        item.Status = MapState(e.NewState, item);

        if (manager.Torrent is not null)
        {
            item.TotalBytes = manager.Torrent.Size;
            if (item.Name.Length == 40 || string.IsNullOrWhiteSpace(item.Name))
                item.Name = manager.Torrent.Name;
        }

        if (e.NewState == TorrentState.Error)
        {
            item.Status = DownloadStatus.Error;
            item.Error = manager.Error?.Exception?.Message ?? "Unknown torrent error";
        }

        // Torrent finished downloading -> it transitions to Seeding.
        if (e.NewState == TorrentState.Seeding && !item.CompleteNotificationSent)
        {
            item.CompleteNotificationSent = true;
            item.CompletedAt = DateTime.UtcNow;
            item.Progress = 100;
            _ = _notifications.DispatchAsync(new NotificationEvent(
                NotificationKind.DownloadCompleted, "Download finished", item.Name));

            // Honour the user's post-download preference. Stopping fires another state change
            // (-> Stopped) which MapState turns into Completed since progress is 100.
            if (await GetPostDownloadActionAsync() == PostDownloadAction.StopSeeding)
            {
                item.Status = DownloadStatus.Completed;
                await manager.StopAsync();
            }
        }

        await PersistAsync(item);
        DownloadsChanged?.Invoke();
    }

    private async Task FailMetadataTimeoutAsync(int id, DownloadItem item, TorrentManager manager)
    {
        if (_removing.ContainsKey(id) || !_metadataFailed.TryAdd(id, 1))
            return;

        try
        {
            item.Status = DownloadStatus.Error;
            item.Error = "No peers found — the torrent may be dead or have no seeders.";
            item.DownloadSpeed = 0;
            item.UploadSpeed = 0;
            item.Peers = 0;

            if (manager.State != TorrentState.Stopped)
                await manager.StopAsync();

            await PersistAsync(item);
            DownloadsChanged?.Invoke();
            _logger.LogInformation("Gave up fetching metadata for {Name} after {Minutes} min (no peers)",
                item.Name, MetadataTimeout.TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to time out metadata fetch for {Name}", item.Name);
        }
    }

    private async Task<PostDownloadAction> GetPostDownloadActionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.GetSettingsAsync()).PostDownloadAction;
    }

    private DownloadStatus MapState(TorrentState state, DownloadItem item) => state switch
    {
        TorrentState.Downloading => DownloadStatus.Downloading,
        TorrentState.Seeding => DownloadStatus.Seeding,
        TorrentState.Paused => DownloadStatus.Paused,
        TorrentState.Hashing or TorrentState.HashingPaused => DownloadStatus.Downloading,
        TorrentState.Metadata => DownloadStatus.FetchingMetadata,
        TorrentState.Error => DownloadStatus.Error,
        TorrentState.Stopped or TorrentState.Stopping =>
            item.Progress >= 100 ? DownloadStatus.Completed : DownloadStatus.Paused,
        _ => item.Status
    };

    public async Task PauseAsync(int id)
    {
        if (_managers.TryGetValue(id, out var manager))
            await manager.PauseAsync();
        if (_items.TryGetValue(id, out var item))
        {
            item.Status = DownloadStatus.Paused;
            await PersistAsync(item);
        }
        DownloadsChanged?.Invoke();
    }

    public async Task ResumeAsync(int id)
    {
        // Let a previously timed-out download report its state again and clear the error.
        _metadataFailed.TryRemove(id, out _);
        _metadataSince.TryRemove(id, out _);
        if (_items.TryGetValue(id, out var item) && item.Status == DownloadStatus.Error)
        {
            item.Error = null;
            item.Status = DownloadStatus.Queued;
        }

        if (_managers.TryGetValue(id, out var manager))
        {
            await manager.StartAsync();
        }
        else if (_items.TryGetValue(id, out var stopped))
        {
            await AttachAndStartAsync(stopped, startPaused: false);
        }
        DownloadsChanged?.Invoke();
    }

    public async Task DeleteAsync(int id, bool deleteFiles)
    {
        // Mark as removing and drop from the live list up front, so the state-change
        // handler and periodic saver stop touching this row while we tear it down
        // (stopping a seeding torrent fires TorrentStateChanged, which would otherwise
        // race the delete and throw a concurrency exception).
        _removing[id] = 1;
        _items.TryRemove(id, out _);
        _metadataSince.TryRemove(id, out _);
        _metadataFailed.TryRemove(id, out _);
        DownloadsChanged?.Invoke();

        try
        {
            if (_managers.TryRemove(id, out var manager) && _engine is not null)
            {
                // Capture the content directory before removal, while the manager still has its paths.
                var contentDir = TryGetContainingDirectory(manager);

                if (manager.State != TorrentState.Stopped)
                    await manager.StopAsync();
                await _engine.RemoveAsync(manager,
                    deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);

                // RemoveAsync deletes the files but leaves the torrent's now-empty folder behind.
                if (deleteFiles)
                    TryDeleteContentDirectory(contentDir, manager.SavePath);
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.Downloads.Where(d => d.Id == id).ExecuteDeleteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete download {Id}", id);
        }
        finally
        {
            _removing.TryRemove(id, out _);
        }
    }

    private string? TryGetContainingDirectory(TorrentManager manager)
    {
        // For a multi-file torrent this is SavePath/<TorrentName>; for a single-file torrent or a
        // magnet without metadata yet it is just SavePath (which we must never delete).
        try { return manager.ContainingDirectory; }
        catch { return null; }
    }

    /// <summary>
    /// Deletes a torrent's leftover content folder, but only when it is a per-torrent subdirectory
    /// of the shared save root — never the save root itself, and never a path outside it.
    /// </summary>
    private void TryDeleteContentDirectory(string? contentDir, string savePath)
    {
        if (string.IsNullOrEmpty(contentDir) || !Directory.Exists(contentDir))
            return;

        try
        {
            var content = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentDir));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(savePath));
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(content, root, comparison))
                return; // single-file torrent — the save root is shared, leave it alone
            if (!content.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return; // unexpected path outside the save root — don't touch it

            Directory.Delete(content, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove leftover folder {Dir}", contentDir);
        }
    }

    private void OnTick()
    {
        bool anyActive = false;
        foreach (var (id, manager) in _managers)
        {
            if (!_items.TryGetValue(id, out var item)) continue;

            // Give up on a download that can't find peers to fetch its metadata from.
            if (manager.State == TorrentState.Metadata)
            {
                var since = _metadataSince.GetOrAdd(id, _ => DateTime.UtcNow);
                if (DateTime.UtcNow - since > MetadataTimeout)
                {
                    _metadataSince.TryRemove(id, out _);
                    _ = FailMetadataTimeoutAsync(id, item, manager);
                    continue;
                }
            }
            else
            {
                _metadataSince.TryRemove(id, out _);
            }

            if (manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata or TorrentState.Hashing)
            {
                anyActive = true;
                item.Progress = Math.Round(manager.Progress, 2);
                item.DownloadSpeed = manager.Monitor.DownloadRate;
                item.UploadSpeed = manager.Monitor.UploadRate;
                item.Peers = manager.OpenConnections;
                if (manager.Torrent is not null)
                    item.TotalBytes = manager.Torrent.Size;
            }
            else
            {
                item.DownloadSpeed = 0;
                item.UploadSpeed = 0;
                item.Peers = 0;
            }
        }

        if (anyActive)
            DownloadsChanged?.Invoke();

        // Persist progress every ~20 seconds so a crash loses very little state.
        if (++_ticks % 10 == 0)
            _ = SaveProgressToDbAsync();
    }

    private async Task SaveProgressToDbAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            foreach (var item in _items.Values)
            {
                db.Downloads.Attach(item);
                db.Entry(item).State = EntityState.Modified;
            }
            await db.SaveChangesAsync();
            foreach (var item in _items.Values)
                db.Entry(item).State = EntityState.Detached;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic progress save failed");
        }
    }

    private async Task PersistAsync(DownloadItem item)
    {
        // Skip items that are being (or have been) deleted.
        if (_removing.ContainsKey(item.Id))
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Downloads.Update(item);
            await db.SaveChangesAsync();
            db.Entry(item).State = EntityState.Detached;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Row was deleted underneath us (e.g. a delete raced this save) — nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving download {Name} failed", item.Name);
        }
    }
}
