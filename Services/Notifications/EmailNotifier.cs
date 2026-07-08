using System.Net;
using System.Net.Mail;
using MediaDownloader.Data.Entities;

namespace MediaDownloader.Services.Notifications;

public class EmailNotifier : INotifier
{
    public string Name => "Email";

    public bool IsEnabled(AppSettings s) =>
        s.EmailEnabled &&
        !string.IsNullOrWhiteSpace(s.SmtpHost) &&
        !string.IsNullOrWhiteSpace(s.EmailTo);

    public async Task NotifyAsync(NotificationEvent evt, AppSettings s, CancellationToken ct = default)
    {
        using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
        {
            EnableSsl = s.SmtpUseSsl,
            Credentials = string.IsNullOrEmpty(s.SmtpUsername)
                ? null
                : new NetworkCredential(s.SmtpUsername, s.SmtpPassword)
        };

        var from = string.IsNullOrWhiteSpace(s.EmailFrom) ? s.SmtpUsername : s.EmailFrom;
        using var message = new MailMessage(from, s.EmailTo, $"[MediaDownloader] {evt.Title}", evt.Message);
        await client.SendMailAsync(message, ct);
    }
}
