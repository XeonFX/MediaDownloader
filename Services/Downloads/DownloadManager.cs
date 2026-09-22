using System.Collections.Concurrent;
using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    private readonly string _cacheDirectory;
    private readonly ITorrentEngineFactory _engineFactory;

    /// <summary>
    /// How long a download may sit fetching magnet metadata before we give up and mark it failed.
    /// A dead torrent (no seeders) otherwise shows "Fetching metadata" forever with no feedback.
    /// Configurable via the "DownloadEngine:MetadataTimeoutMinutes" setting.
    /// </summary>
    private readonly TimeSpan _metadataTimeout;

    private ClientEngine? _engine;
    private readonly ConcurrentDictionary<int, TorrentManager> _managers = new();
    private readonly ConcurrentDictionary<int, DownloadItem> _items = new();
    private readonly ConcurrentDictionary<int, byte> _removing = new();
    // When each download first entered the metadata-fetching state, used to enforce _metadataTimeout.
    private readonly ConcurrentDictionary<int, DateTime> _metadataSince = new();
    // Downloads we've failed for a metadata timeout; the state handler ignores their stop transition.
    private readonly ConcurrentDictionary<int, byte> _metadataFailed = new();
    private Timer? _timer;
    private int _ticks;
    // Set during shutdown so the stop-induced state changes don't overwrite each download's live
    // status (Downloading/FetchingMetadata) with Paused — otherwise they wouldn't auto-resume on restart.
    private volatile bool _shuttingDown;

    // Guards every mutation of a live DownloadItem's persisted fields. The 2s timer (OnTick) and
    // MonoTorrent's TorrentStateChanged event both mutate the same DownloadItem instances that are
    // also bound in the Blazor UI and handed to EF Core for saving — without this, EF's
    // change-tracking could read a field mid-mutation from another thread while building an UPDATE.
    // Snapshot() (below) takes the lock to copy values into a fresh, never-mutated-again object
    // before anything is handed to EF, so the actual DB I/O never touches a live object.
    private readonly Lock _stateLock = new();

    // Serializes the check-for-existing-hash + DB-insert section of AddDownloadAsync/
    // AddDownloadFromTorrentFileAsync. Without it, a manual add and a SeriesMonitor add for the same
    // torrent landing at the same instant can both pass the "does _items already have this hash"
    // check before either has inserted, creating two rows (and attaching the torrent twice). The DB
    // unique index on InfoHash is the backstop; this lock avoids ever hitting it in practice.
    private readonly SemaphoreSlim _addLock = new(1, 1);

    /// <summary>Raised whenever download progress/state changes; UI pages subscribe to refresh.</summary>
    public event Action? DownloadsChanged;

    public DownloadManager(
        IDbContextFactory<AppDbContext> dbFactory,
        NotificationDispatcher notifications,
        IOptions<DownloadEngineOptions> options,
        ITorrentEngineFactory engineFactory,
        ILogger<DownloadManager> logger)
    {
        _dbFactory = dbFactory;
        _notifications = notifications;
        _logger = logger;
        _metadataTimeout = TimeSpan.FromMinutes(options.Value.MetadataTimeoutMinutes);
        _cacheDirectory = string.IsNullOrWhiteSpace(options.Value.CacheDirectory)
            ? AppPaths.TorrentCacheDirectory
            : Path.GetFullPath(options.Value.CacheDirectory);
        _engineFactory = engineFactory;
    }

    public IReadOnlyList<DownloadItem> GetDownloads() =>
        _items.Values.OrderByDescending(i => i.AddedAt).ToList();

    public async Task StartAsync(CancellationToken ct)
    {
        // Null only in tests, which run the manager and everything above it without opening peer,
        // DHT or tracker sockets. Every engine-touching path below already had to handle a null
        // engine, so that is the single condition the no-engine mode rides on.
        _engine = _engineFactory.Create();

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

        foreach (var item in _items.Values.Where(i => _engine is not null
                     && i.Status != DownloadStatus.Completed && i.Status != DownloadStatus.Error))
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

        if (_engine is not null)
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
        // _addLock is intentionally not disposed: an AddDownload* call racing shutdown would
        // otherwise hit ObjectDisposedException on WaitAsync. A SemaphoreSlim whose AvailableWaitHandle
        // was never accessed holds no unmanaged resource, so leaving it to the GC is safe.
    }

    public Task<DownloadItem> AddDownloadAsync(string name, string magnetUri, string source,
        int? seriesTaskId = null, string? saveFolder = null)
    {
        var magnet = MagnetLink.Parse(magnetUri);
        var hash = magnet.InfoHashes.V1OrV2.ToHex();

        return AddOrGetExistingAsync(hash, saveFolder, savePath => new DownloadItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? magnet.Name ?? hash : name,
            NameIsPlaceholder = string.IsNullOrWhiteSpace(name) && string.IsNullOrEmpty(magnet.Name),
            MagnetUri = magnetUri,
            InfoHash = hash,
            SavePath = savePath,
            Source = source,
            Status = DownloadStatus.Queued,
            SeriesTaskId = seriesTaskId
        });
    }

    /// <summary>
    /// Adds a download from raw .torrent file contents (private trackers serve these instead of
    /// magnet links). The file is cached on disk so the download can be re-attached after a restart
    /// without talking to the tracker again.
    /// </summary>
    public async Task<DownloadItem> AddDownloadFromTorrentFileAsync(string name, byte[] torrentBytes, string source,
        int? seriesTaskId = null, string? saveFolder = null)
    {
        var torrent = Torrent.Load(torrentBytes);
        var hash = torrent.InfoHashes.V1OrV2.ToHex();

        // Keyed by hash, so writing it before the lock is safe even if two callers race for the
        // same torrent — both write identical bytes to the same path.
        var torrentDir = Path.Combine(_cacheDirectory, "torrent-files");
        Directory.CreateDirectory(torrentDir);
        var torrentPath = Path.Combine(torrentDir, hash + ".torrent");
        await File.WriteAllBytesAsync(torrentPath, torrentBytes);

        return await AddOrGetExistingAsync(hash, saveFolder, savePath => new DownloadItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? torrent.Name : name,
            TorrentFilePath = torrentPath,
            InfoHash = hash,
            SavePath = savePath,
            Source = source,
            Status = DownloadStatus.Queued,
            SeriesTaskId = seriesTaskId
        });
    }

    /// <summary>
    /// Shared add flow for both the magnet and .torrent-file paths: under <see cref="_addLock"/>,
    /// return the already-tracked download for this hash if there is one, otherwise persist a new
    /// row built by <paramref name="createItem"/> (given the resolved save path), attach it to the
    /// engine and start it. The lock closes the check-then-insert race that the DB unique index on
    /// InfoHash backstops.
    /// </summary>
    private async Task<DownloadItem> AddOrGetExistingAsync(string hash, string? saveFolder,
        Func<string, DownloadItem> createItem)
    {
        DownloadItem item;
        await _addLock.WaitAsync();
        try
        {
            var existing = _items.Values.FirstOrDefault(i => i.InfoHash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing; // already tracked — nothing new to attach

            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.GetSettingsAsync();
            var savePath = string.IsNullOrWhiteSpace(saveFolder) ? settings.DownloadFolder : saveFolder;
            Directory.CreateDirectory(savePath);

            item = createItem(savePath);
            db.Downloads.Add(item);
            await db.SaveChangesAsync();
            _items[item.Id] = item;
        }
        finally
        {
            _addLock.Release();
        }

        if (_engine is not null)
            await AttachAndStartAsync(item, startPaused: false);
        DownloadsChanged?.Invoke();
        return item;
    }

    private async Task AttachAndStartAsync(DownloadItem item, bool startPaused)
    {
        if (_engine is null) throw new InvalidOperationException("Engine not started");

        var manager = item.TorrentFilePath is { Length: > 0 } torrentPath && File.Exists(torrentPath)
            ? await _engine.AddAsync(await Torrent.LoadAsync(torrentPath), item.SavePath)
            : await _engine.AddAsync(MagnetLink.Parse(item.MagnetUri), item.SavePath);
        _managers[item.Id] = manager;

        manager.TorrentStateChanged += async (_, e) =>
        {
            try { await OnTorrentStateChangedAsync(item, manager, e); }
            catch (Exception ex) { _logger.LogError(ex, "State change handling failed for {Name}", item.Name); }
        };

        if (!startPaused)
        {
            await manager.StartAsync();
            lock (_stateLock) { item.Status = DownloadStatus.FetchingMetadata; }

            if (!item.StartNotificationSent)
            {
                lock (_stateLock) { item.StartNotificationSent = true; }
                await PersistAsync(item);
                _ = FireAndForget.RunSafe(
                    () => _notifications.DispatchAsync(new NotificationEvent(
                        NotificationKind.DownloadStarted, "Download started", item.Name)),
                    _logger, "Failed to dispatch download-started notification for {Name}", item.Name);
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

        bool justCompleted;
        lock (_stateLock)
        {
            item.Status = MapState(e.NewState, item);

            if (manager.Torrent is not null)
            {
                item.TotalBytes = manager.Torrent.Size;
                if (item.NameIsPlaceholder || string.IsNullOrWhiteSpace(item.Name))
                {
                    item.Name = manager.Torrent.Name;
                    item.NameIsPlaceholder = false;
                }
            }

            if (e.NewState == TorrentState.Error)
            {
                item.Status = DownloadStatus.Error;
                item.Error = manager.Error?.Exception?.Message ?? "Unknown torrent error";
            }

            // Torrent finished downloading -> it transitions to Seeding.
            justCompleted = e.NewState == TorrentState.Seeding && !item.CompleteNotificationSent;
            if (justCompleted)
            {
                item.CompleteNotificationSent = true;
                item.CompletedAt = DateTime.UtcNow;
                item.Progress = 100;
            }
        }

        if (justCompleted)
        {
            _ = FireAndForget.RunSafe(
                () => _notifications.DispatchAsync(new NotificationEvent(
                    NotificationKind.DownloadCompleted, "Download finished", item.Name)),
                _logger, "Failed to dispatch download-completed notification for {Name}", item.Name);

            // Honour the user's post-download preference. Stopping fires another state change
            // (-> Stopped) which MapState turns into Completed since progress is 100.
            if (await GetPostDownloadActionAsync() == PostDownloadAction.StopSeeding)
            {
                lock (_stateLock) { item.Status = DownloadStatus.Completed; }
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

        lock (_stateLock)
        {
            item.Status = DownloadStatus.Error;
            item.Error = "No peers found — the torrent may be dead or have no seeders.";
            item.DownloadSpeed = 0;
            item.UploadSpeed = 0;
            item.Peers = 0;
        }

        if (manager.State != TorrentState.Stopped)
            await manager.StopAsync();

        await PersistAsync(item);
        DownloadsChanged?.Invoke();
        _logger.LogInformation("Gave up fetching metadata for {Name} after {Minutes} min (no peers)",
            item.Name, _metadataTimeout.TotalMinutes);
    }

    private async Task<PostDownloadAction> GetPostDownloadActionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return (await db.GetSettingsAsync()).PostDownloadAction;
    }

    // Static and internal (rather than private): it reads no instance state, only its parameters,
    // so it's directly unit-testable without standing up a whole DownloadManager + engine.
    internal static DownloadStatus MapState(TorrentState state, DownloadItem item) => state switch
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
            lock (_stateLock) { item.Status = DownloadStatus.Paused; }
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
            lock (_stateLock)
            {
                item.Error = null;
                item.Status = DownloadStatus.Queued;
            }
        }

        if (_engine is null)
        {
            if (_items.TryGetValue(id, out var queued))
            {
                lock (_stateLock) { queued.Status = DownloadStatus.Queued; }
                await PersistAsync(queued);
            }
        }
        else if (_managers.TryGetValue(id, out var manager))
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
        _items.TryRemove(id, out var removedItem);
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

            // Drop the cached .torrent file (private-tracker downloads) along with the row.
            if (removedItem?.TorrentFilePath is { Length: > 0 } cached && File.Exists(cached))
                File.Delete(cached);
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

            // MonoTorrent has already removed the files it owns. A user may have put other
            // files here, or another torrent may share this directory. Never remove those.
            DownloadDirectoryCleanup.RemoveEmptyTree(content);
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
                if (DateTime.UtcNow - since > _metadataTimeout)
                {
                    _metadataSince.TryRemove(id, out _);
                    _ = FireAndForget.RunSafe(() => FailMetadataTimeoutAsync(id, item, manager),
                        _logger, "Failed to time out metadata fetch for {Name}", item.Name);
                    continue;
                }
            }
            else
            {
                _metadataSince.TryRemove(id, out _);
            }

            lock (_stateLock)
            {
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
        }

        // Fans out to every open circuit, each of which re-reads GetDownloads() and re-renders its
        // table. Fine at this app's scale (a handful of downloads, a tab or two); if the download
        // count or concurrent-tab count ever grew large, this 2s cadence is the first thing to
        // throttle or move behind row virtualization.
        if (anyActive)
            DownloadsChanged?.Invoke();

        // Persist progress every ~20 seconds so a crash loses very little state.
        if (++_ticks % 10 == 0)
            _ = FireAndForget.RunSafe(SaveProgressToDbAsync, _logger, "Periodic progress save failed");
    }

    private async Task SaveProgressToDbAsync()
    {
        // Only items whose progress/speed can actually still be changing — Completed/Error/Paused
        // rows are static until a user or state-change event acts on them (which already persists
        // through PersistAsync). Skipping them avoids rewriting the whole download history every
        // ~20 seconds as it grows.
        var active = _items.Values.Where(i => i.Status is
            DownloadStatus.Downloading or DownloadStatus.Seeding or
            DownloadStatus.FetchingMetadata or DownloadStatus.Queued);

        // Snapshot before opening the DbContext, so the whole batch reflects a consistent instant
        // and nothing awaited below can read a field mid-mutation.
        var snapshots = active.Select(Snapshot).ToList();
        if (snapshots.Count == 0)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        foreach (var snapshot in snapshots)
        {
            db.Downloads.Attach(snapshot);
            db.Entry(snapshot).State = EntityState.Modified;
        }

        // If a row was deleted underneath us (a delete raced this save), EF throws
        // DbUpdateConcurrencyException for that entry — and rolling back the whole batch would drop
        // every *other* active download's progress this tick too. Detach the vanished rows and
        // retry so the survivors still persist; the next tick's snapshot omits the deleted ones.
        while (true)
        {
            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (ex.Entries.Count > 0)
            {
                foreach (var entry in ex.Entries)
                    entry.State = EntityState.Detached;
            }
        }
    }

    private async Task PersistAsync(DownloadItem item)
    {
        // Skip items that are being (or have been) deleted.
        if (_removing.ContainsKey(item.Id))
            return;

        var snapshot = Snapshot(item);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Downloads.Update(snapshot);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Row was deleted underneath us (e.g. a delete raced this save) — nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving download {Name} failed", snapshot.Name);
        }
    }

    /// <summary>
    /// Copies a live DownloadItem's persisted fields into a new, detached instance under
    /// <see cref="_stateLock"/>, so EF Core's change-tracking/SaveChanges (which can take a while
    /// on the DB I/O path) never reads a field that OnTick or the MonoTorrent state-change handler
    /// could be mutating concurrently on another thread. The [NotMapped] runtime-only stats
    /// (DownloadSpeed/UploadSpeed/Peers) are intentionally omitted — EF never persists them.
    /// </summary>
    private DownloadItem Snapshot(DownloadItem item)
    {
        lock (_stateLock)
        {
            return new DownloadItem
            {
                Id = item.Id,
                Name = item.Name,
                NameIsPlaceholder = item.NameIsPlaceholder,
                MagnetUri = item.MagnetUri,
                TorrentFilePath = item.TorrentFilePath,
                InfoHash = item.InfoHash,
                SavePath = item.SavePath,
                Source = item.Source,
                Status = item.Status,
                Progress = item.Progress,
                TotalBytes = item.TotalBytes,
                AddedAt = item.AddedAt,
                CompletedAt = item.CompletedAt,
                Error = item.Error,
                StartNotificationSent = item.StartNotificationSent,
                CompleteNotificationSent = item.CompleteNotificationSent,
                SeriesTaskId = item.SeriesTaskId
            };
        }
    }
}
