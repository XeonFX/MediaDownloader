using MediaDownloader.Data.Entities;

namespace MediaDownloader.Services.Notifications;

/// <summary>
/// Abstraction for a notification channel. Implement this interface and register it in
/// Program.cs (builder.Services.AddSingleton&lt;INotifier, MyNotifier&gt;())
/// to add a new notification method.
/// </summary>
public interface INotifier
{
    string Name { get; }

    /// <summary>Whether this channel is currently enabled/configured according to user settings.</summary>
    bool IsEnabled(AppSettings settings);

    Task NotifyAsync(NotificationEvent evt, AppSettings settings, CancellationToken ct = default);
}
