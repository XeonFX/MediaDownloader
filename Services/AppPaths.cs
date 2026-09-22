namespace MediaDownloader.Services;

/// <summary>
/// Central location for the app's writable data (database, torrent cache).
/// On macOS this is ~/Library/Application Support/MediaDownloader; on Windows it is the current
/// user's LocalApplicationData/MediaDownloader. Both locations are per-user and writable without
/// exposing endpoint credentials next to a machine-wide executable. Linux keeps the old behavior.
/// </summary>
public static class AppPaths
{
    // Packaged macOS builds keep non-code content outside Contents/MacOS so codesign can
    // seal it as resources. Development and Windows retain the normal publish layout.
    public static string ContentDirectory { get; } = ResolveContentDirectory();
    public static string DataDirectory { get; } = Resolve();

    private static string ResolveContentDirectory()
    {
        var executableDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        if (OperatingSystem.IsMacOS() && executableDirectory.Name == "MacOS"
            && executableDirectory.Parent is { Name: "Contents" } contents)
            return Path.Combine(contents.FullName, "Resources");
        return AppContext.BaseDirectory;
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "mediadownloader.db");
    public static string TorrentCacheDirectory => Path.Combine(DataDirectory, "torrent-cache");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public static string AgentEndpointPath => Path.Combine(DataDirectory, "endpoint.json");

    private static string Resolve()
    {
        // Also permits isolated packaged-app smoke tests without touching an installed user's DB.
        var configured = Environment.GetEnvironmentVariable("MD_DATA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var custom = Path.GetFullPath(configured);
            Directory.CreateDirectory(custom);
            return custom;
        }
        string dir;
        if (OperatingSystem.IsMacOS())
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "MediaDownloader");
        }
        else if (OperatingSystem.IsWindows())
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaDownloader");
        }
        else
        {
            return AppContext.BaseDirectory;
        }

        Directory.CreateDirectory(dir);
        MigrateLegacyData(dir);
        return dir;
    }

    /// <summary>
    /// One-time copy of data created by older builds next to the executable. Data-protection keys
    /// must travel with the database or encrypted provider credentials and agent tokens would no
    /// longer decrypt after the Windows/macOS path migration.
    /// </summary>
    private static void MigrateLegacyData(string dir)
    {
        var newDb = Path.Combine(dir, "mediadownloader.db");
        var oldDb = Path.Combine(AppContext.BaseDirectory, "mediadownloader.db");
        if (!File.Exists(newDb) && File.Exists(oldDb))
        {
            // Copy the WAL/SHM sidecars too so uncheckpointed writes aren't lost.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = oldDb + suffix;
                if (File.Exists(src))
                    File.Copy(src, newDb + suffix);
            }
        }

        var oldKeys = Path.Combine(AppContext.BaseDirectory, "keys");
        var newKeys = Path.Combine(dir, "keys");
        if (Directory.Exists(oldKeys) && !Directory.Exists(newKeys))
            CopyDirectory(oldKeys, newKeys);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var child in Directory.EnumerateDirectories(source))
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}
