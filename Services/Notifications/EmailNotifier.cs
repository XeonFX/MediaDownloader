using MailKit.Net.Smtp;
using MailKit.Security;
using MediaDownloader.Data.Entities;
using MimeKit;

namespace MediaDownloader.Services.Notifications;

/// <summary>
/// Sends via MailKit rather than System.Net.Mail.SmtpClient, which Microsoft's own docs mark as not
/// recommended for new development (no modern auth support, known connection-handling issues).
/// </summary>
public class EmailNotifier : INotifier
{
    public string Name => "Email";

    public bool IsEnabled(AppSettings s) =>
        s.EmailEnabled &&
        !string.IsNullOrWhiteSpace(s.SmtpHost) &&
        !string.IsNullOrWhiteSpace(s.EmailTo);

    public async Task NotifyAsync(NotificationEvent evt, AppSettings s, CancellationToken ct = default)
    {
        var from = string.IsNullOrWhiteSpace(s.EmailFrom) ? s.SmtpUsername : s.EmailFrom;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(s.EmailTo));
        message.Subject = $"[MediaDownloader] {evt.Title}";
        message.Body = new TextPart("plain") { Text = evt.Message };

        using var client = new SmtpClient();
        var secureOption = s.SmtpUseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
        await client.ConnectAsync(s.SmtpHost, s.SmtpPort, secureOption, ct);
        if (!string.IsNullOrEmpty(s.SmtpUsername))
            await client.AuthenticateAsync(s.SmtpUsername, s.SmtpPassword, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
