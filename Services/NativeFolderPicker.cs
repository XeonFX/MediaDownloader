using System.Diagnostics;

namespace MediaDownloader.Services;

/// <summary>
/// Opens the native macOS folder chooser. The Blazor UI runs in a browser, but the server is the
/// user's own machine, so we can show a real NSOpenPanel via <c>osascript</c>'s "choose folder"
/// and hand the picked path back to the page. On other platforms <see cref="IsSupported"/> is
/// false and callers fall back to the in-browser <c>FolderBrowserDialog</c>.
/// </summary>
public class NativeFolderPicker
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Shows the system folder chooser. Returns the picked path, or null if cancelled.</summary>
    public async Task<string?> PickFolderAsync(string? startFolder = null, string prompt = "Choose a folder")
    {
        if (!IsSupported)
            return null;

        // AppleScript string literal escaping for the embedded prompt/path.
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var script = $"POSIX path of (choose folder with prompt \"{Esc(prompt)}\"";
        if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
            script += $" default location POSIX file \"{Esc(startFolder)}\"";
        script += ")";

        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                return null; // cancelled (or scripting failure) — caller keeps the current value

            var path = output.Trim();
            // "POSIX path of" yields a trailing slash ("/Users/x/Downloads/") — normalize it away.
            return path.Length > 1 ? path.TrimEnd('/') : (path.Length == 0 ? null : path);
        }
        catch
        {
            return null;
        }
    }
}
