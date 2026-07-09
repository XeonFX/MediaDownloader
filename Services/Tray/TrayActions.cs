using System.Diagnostics;
using MediaDownloader.Services.Updates;

namespace MediaDownloader.Services.Tray;

/// <summary>
/// Effects behind a <see cref="TrayAction"/>, shared by <see cref="MacTrayApp"/> and
/// <see cref="WindowsTrayApp"/> so "what Quit/Dashboard/Update mean" lives in one place.
/// </summary>
internal static class TrayActions
{
    /// <summary>Opens a URL in the default browser.</summary>
    internal static void OpenUrl(string url)
    {
        try
        {
            // macOS: shell out to `open` directly (verified working; UseShellExecute's `open`
            // handoff isn't as reliable when launched from a Finder-opened .app bundle).
            // Everywhere else, UseShellExecute=true is the standard way to hand a URL to the OS.
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    internal static void CheckUpdates(UpdateService updates)
    {
        // Result surfaces on the next menu open ("up to date, checked HH:mm" / update item)
        // and through a notification if a new version is found.
        try { _ = Task.Run(() => updates.CheckNowAsync()); } catch { }
    }

    internal static void InstallUpdate(UpdateService updates)
    {
        // Runs the download/install off the tray's event-loop thread; on success (macOS) the
        // service stops the host and a handoff script relaunches the new bundle. Elsewhere it
        // just opens the release page (see UpdateService.InstallAsync).
        try { _ = Task.Run(() => updates.InstallAsync()); } catch { }
    }

    internal static void Quit(WebApplication app)
    {
        try { app.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); } catch { }
        Environment.Exit(0);
    }
}
