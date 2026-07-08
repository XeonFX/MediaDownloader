using MediaDownloader.Data.Entities;

namespace MediaDownloader.Services.Notifications;

/// <summary>
/// Desktop notifications shown through the browser's Notification API.
/// The MainLayout of every connected circuit subscribes to <see cref="Notified"/>
/// and forwards events to JavaScript.
/// </summary>
public class DesktopNotifier : INotifier
{
    public string Name => "Desktop";

    public event Func<NotificationEvent, Task>? Notified;

    public bool IsEnabled(AppSettings s) => s.DesktopEnabled;

    public async Task NotifyAsync(NotificationEvent evt, AppSettings s, CancellationToken ct = default)
    {
        var handlers = Notified;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<NotificationEvent, Task>>())
        {
            try { await handler(evt); }
            catch { /* circuit may have disconnected */ }
        }
    }
}
