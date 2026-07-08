namespace MediaDownloader.Services.Notifications;

public enum NotificationKind
{
    DownloadStarted,
    DownloadCompleted,
    Error,
    UpdateAvailable
}

public record NotificationEvent(NotificationKind Kind, string Title, string Message);
