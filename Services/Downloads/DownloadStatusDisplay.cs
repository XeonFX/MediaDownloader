using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Localization;
using MudBlazor;

namespace MediaDownloader.Services.Downloads;

/// <summary>
/// Shared status/progress display mapping and delete-confirmation flow for a
/// <see cref="DownloadItem"/>, used by both the Downloads and Series Tasks pages so the two don't
/// drift out of sync.
/// </summary>
public static class DownloadStatusDisplay
{
    public static Color ProgressColor(DownloadItem d) => d.Status switch
    {
        DownloadStatus.Completed or DownloadStatus.Seeding => Color.Success,
        DownloadStatus.Error => Color.Error,
        DownloadStatus.Paused => Color.Warning,
        _ => Color.Primary
    };

    public static Color StatusColor(DownloadStatus s) => s switch
    {
        DownloadStatus.Downloading => Color.Primary,
        DownloadStatus.Seeding or DownloadStatus.Completed => Color.Success,
        DownloadStatus.Paused => Color.Warning,
        DownloadStatus.Error => Color.Error,
        _ => Color.Default
    };

    public static string StatusText(DownloadStatus s, LocalizationService l) => s switch
    {
        DownloadStatus.Queued => l["status.queued"],
        DownloadStatus.FetchingMetadata => l["status.fetchingMetadata"],
        DownloadStatus.Downloading => l["status.downloading"],
        DownloadStatus.Seeding => l["status.seeding"],
        DownloadStatus.Paused => l["status.paused"],
        DownloadStatus.Completed => l["status.completed"],
        DownloadStatus.Error => l["status.error"],
        _ => s.ToString()
    };

    /// <summary>Prompts to delete a download (optionally its files too) and applies the choice.</summary>
    public static async Task ConfirmDeleteAsync(
        IDialogService dialogService, DownloadManager downloadManager, LocalizationService l, DownloadItem item)
    {
        var deleteFiles = await dialogService.ShowMessageBoxAsync(
            l["downloads.deleteTitle"],
            l.Format("downloads.deleteMessage", item.Name),
            yesText: l["downloads.deleteFiles"], noText: l["downloads.keepFiles"], cancelText: l["common.cancel"]);

        if (deleteFiles is null) return;
        await downloadManager.DeleteAsync(item.Id, deleteFiles.Value);
    }
}
