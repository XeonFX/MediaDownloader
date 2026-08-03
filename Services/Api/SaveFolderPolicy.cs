namespace MediaDownloader.Services.Api;

/// <summary>
/// Confines an agent-supplied save folder to the configured download root.
///
/// Without this, <c>start_download</c>'s <c>folder</c> argument is an arbitrary filesystem write:
/// the folder is created on demand, and the filenames inside a torrent come from its metadata, so a
/// chosen folder plus a chosen torrent writes attacker-controlled content anywhere the user can
/// write — a login item, a shell rc directory, anything. That is not hypothetical for an agent: it
/// reads torrent titles and descriptions fetched from the internet and then picks tool arguments, so
/// a prompt injection in a description ("save it to ~/Library/LaunchAgents") closes the loop without
/// the user doing anything wrong.
///
/// The Blazor UI is unaffected — a human picks a folder through the native browser, and that path
/// never comes through here.
/// </summary>
public static class SaveFolderPolicy
{
    /// <summary>
    /// Returns the folder to hand the download engine: null to use the default, or an absolute path
    /// verified to sit inside <paramref name="downloadRoot"/>.
    /// </summary>
    /// <exception cref="AgentApiException">The path escapes the root, or isn't usable as one.</exception>
    public static string? Resolve(string? requested, string downloadRoot)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return null; // caller falls back to the configured default

        string candidate, root;
        try
        {
            candidate = Canonicalize(requested);
            root = Canonicalize(downloadRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentApiException($"'{requested}' is not a usable folder path.");
        }

        if (!IsWithin(candidate, root))
            throw new AgentApiException(
                $"Downloads can only be saved inside the configured download folder ('{downloadRoot}'). " +
                $"'{requested}' is outside it. Use a relative sub-folder, or change the download folder in Settings.");

        return candidate;
    }

    /// <summary>True when <paramref name="candidate"/> is the root itself or sits beneath it.</summary>
    private static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, root, comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Absolute, separator-normalised path with symlinks resolved as far as the path already exists.
    /// Resolving matters: <c>Path.GetFullPath</c> collapses "..", but a symlink *inside* the
    /// download folder pointing outside it would still pass a purely textual containment check.
    /// The part of the path that doesn't exist yet can't be a link, so resolving the deepest
    /// existing ancestor and re-appending the remainder is exact.
    /// </summary>
    private static string Canonicalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        var remainder = new Stack<string>();
        var current = full;
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return full; // nothing along this path exists; no link can be hiding in it

            remainder.Push(Path.GetFileName(current));
            current = parent;
        }

        var resolved = Directory.ResolveLinkTarget(current, returnFinalTarget: true)?.FullName ?? current;
        return Path.TrimEndingDirectorySeparator(
            remainder.Count == 0 ? resolved : Path.Combine([resolved, .. remainder]));
    }
}
