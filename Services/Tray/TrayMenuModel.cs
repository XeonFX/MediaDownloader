using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Updates;

namespace MediaDownloader.Services.Tray;

/// <summary>Actions a tray menu command can trigger, dispatched by the platform-specific tray host.</summary>
internal enum TrayAction
{
    Dashboard,
    CheckUpdates,
    InstallUpdate,
    Quit,
}

internal abstract record TrayMenuEntry;
/// <summary>A disabled row (status text/header) — no action selector.</summary>
internal sealed record TrayLabel(string Text) : TrayMenuEntry;
internal sealed record TraySeparator : TrayMenuEntry;
internal sealed record TrayCommand(string Text, TrayAction Action) : TrayMenuEntry;

/// <summary>
/// Platform-agnostic tray menu content (rows + button/tooltip text), shared by
/// <see cref="MacTrayApp"/> (renders into an <c>NSMenu</c>) and <see cref="WindowsTrayApp"/>
/// (renders into a Win32 popup menu) so the two native shells stay in sync automatically.
/// </summary>
internal static class TrayMenuModel
{
    /// <summary>Builds the dropdown/context-menu rows from live download and update state.</summary>
    internal static IReadOnlyList<TrayMenuEntry> Build(DownloadManager downloads, UpdateService updates, string dashboardUrl)
    {
        var items = new List<TrayMenuEntry>();

        var active = downloads.GetDownloads()
            .Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Seeding or DownloadStatus.FetchingMetadata)
            .ToList();

        if (active.Count == 0)
        {
            items.Add(new TrayLabel("No active downloads"));
        }
        else
        {
            long totalDown = 0;
            foreach (var d in active)
            {
                totalDown += d.DownloadSpeed;
                items.Add(new TrayLabel(FormatRow(d)));
            }
            items.Add(new TraySeparator());
            items.Add(new TrayLabel($"Total ↓ {ByteSize.FormatRate(totalDown)}"));
        }

        items.Add(new TraySeparator());
        items.Add(new TrayCommand($"Dashboard — {new Uri(dashboardUrl).Authority}", TrayAction.Dashboard));

        items.Add(new TraySeparator());
        if (updates.Installing)
        {
            items.Add(new TrayLabel($"MediaDownloader v{UpdateService.CurrentVersionText}"));
            items.Add(new TrayLabel("Installing update…"));
        }
        else if (updates.Available is { } update)
        {
            items.Add(new TrayLabel($"MediaDownloader v{UpdateService.CurrentVersionText} — {update.TagName} available"));
            // Only macOS (with a detected .app bundle) can swap itself in place; everywhere else
            // InstallAsync falls back to opening the release page instead, so label it honestly.
            var suffix = updates.CanSelfInstall ? "(restarts the app)" : "(opens release page)";
            items.Add(new TrayCommand($"Update to {update.TagName} {suffix}", TrayAction.InstallUpdate));
        }
        else
        {
            var status = updates.LastCheckError is not null ? " — update check failed"
                : updates.LastCheckedAt is { } checkedAt ? $" — up to date, checked {checkedAt:HH:mm}"
                : "";
            items.Add(new TrayLabel($"MediaDownloader v{UpdateService.CurrentVersionText}{status}"));
            if (updates.Checking) items.Add(new TrayLabel("Checking for updates…"));
            else items.Add(new TrayCommand("Check for Updates…", TrayAction.CheckUpdates));
        }

        items.Add(new TraySeparator());
        items.Add(new TrayCommand("Quit MediaDownloader", TrayAction.Quit));
        return items;
    }

    private static string FormatRow(DownloadItem d)
    {
        var name = d.Name.Length > 44 ? d.Name[..43] + "…" : d.Name;
        return d.Status switch
        {
            DownloadStatus.FetchingMetadata => $"{name} — fetching metadata…",
            DownloadStatus.Seeding => $"{name} — ↑ {ByteSize.FormatRate(d.UploadSpeed)} (seeding)",
            _ => $"{name} — ↓ {ByteSize.FormatRate(d.DownloadSpeed)}  {d.Progress:0}%",
        };
    }

    /// <summary>Total active download speed as a formatted rate, or "" when nothing is downloading.</summary>
    internal static string SpeedText(DownloadManager downloads)
    {
        long totalDown = downloads.GetDownloads()
            .Where(d => d.Status is DownloadStatus.Downloading or DownloadStatus.FetchingMetadata)
            .Sum(d => d.DownloadSpeed);
        return totalDown > 0 ? ByteSize.FormatRate(totalDown) : "";
    }
}
