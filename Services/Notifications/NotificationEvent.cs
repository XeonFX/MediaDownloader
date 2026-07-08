namespace MediaDownloader.Services.Notifications;

public enum NotificationKind
{
    DownloadStarted,
    DownloadCompleted,
    Error
}

public record NotificationEvent(NotificationKind Kind, string Title, string Message);
