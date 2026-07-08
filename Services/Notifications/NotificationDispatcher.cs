using MediaDownloader.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Notifications;

/// <summary>Fans a notification event out to every enabled notification channel.</summary>
public class NotificationDispatcher
{
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        IEnumerable<INotifier> notifiers,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<NotificationDispatcher> logger)
    {
        _notifiers = notifiers;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task DispatchAsync(NotificationEvent evt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await db.GetSettingsAsync(ct);

        if (evt.Kind == NotificationKind.DownloadStarted && !settings.NotifyOnStart) return;
        if (evt.Kind == NotificationKind.DownloadCompleted && !settings.NotifyOnComplete) return;

        foreach (var notifier in _notifiers.Where(n => n.IsEnabled(settings)))
        {
            try
            {
                await notifier.NotifyAsync(evt, settings, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notifier {Notifier} failed", notifier.Name);
            }
        }
    }
}
