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
        // Prefer an explicit From; otherwise use the SMTP username only when it's itself a valid
        // address (many SMTP logins are plain usernames, which MailboxAddress.Parse would reject),
        // else fall back to the already-validated To address — so a notification never silently
        // fails to send purely because the From field couldn't be parsed.
        var from = FirstValidAddress(s.EmailFrom, s.SmtpUsername, s.EmailTo);

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

    /// <summary>Returns the first candidate MailKit can parse as an address, or the last one as a last resort.</summary>
    private static string FirstValidAddress(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && MailboxAddress.TryParse(c, out _))
            ?? candidates[^1];
}
