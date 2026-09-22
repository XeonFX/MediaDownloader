namespace MediaDownloader.Services.Downloads;

internal static class DownloadDirectoryCleanup
{
    internal static void RemoveEmptyTree(string directory)
    {
        // Never traverse directory links/junctions when tidying a download's empty folders.
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            return;

        foreach (var child in Directory.EnumerateDirectories(directory))
            RemoveEmptyTree(child);

        try
        {
            // The filesystem checks emptiness at deletion time, including files added while
            // cleanup is running. An enumeration followed by recursive deletion is unsafe.
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException) when (Directory.Exists(directory))
        {
            // Non-empty or busy: leave it in place.
        }
    }
}
