namespace MediaDownloader.Services;

/// <summary>
/// Central location for the app's writable data (database, torrent cache).
/// On macOS this is ~/Library/Application Support/MediaDownloader — writing next to the executable
/// would land inside the .app bundle, which is wiped on every rebuild and read-only once the app
/// is signed. Other platforms keep the old behavior (next to the executable).
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Resolve();

    public static string DatabasePath => Path.Combine(DataDirectory, "mediadownloader.db");
    public static string TorrentCacheDirectory => Path.Combine(DataDirectory, "torrent-cache");

    private static string Resolve()
    {
        if (!OperatingSystem.IsMacOS())
            return AppContext.BaseDirectory;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "MediaDownloader");
        Directory.CreateDirectory(dir);
        MigrateLegacyDatabase(dir);
        return dir;
    }

    /// <summary>
    /// One-time move of a database created by older builds next to the executable
    /// (bin/Debug during dev, or inside a previously built .app bundle).
    /// </summary>
    private static void MigrateLegacyDatabase(string dir)
    {
        var newDb = Path.Combine(dir, "mediadownloader.db");
        var oldDb = Path.Combine(AppContext.BaseDirectory, "mediadownloader.db");
        if (File.Exists(newDb) || !File.Exists(oldDb))
            return;

        // Copy the WAL/SHM sidecars too so uncheckpointed writes aren't lost.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var src = oldDb + suffix;
            if (File.Exists(src))
                File.Copy(src, newDb + suffix);
        }
    }
}
