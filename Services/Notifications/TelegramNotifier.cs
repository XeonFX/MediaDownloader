using System.Net.Http.Json;
using MediaDownloader.Data.Entities;

namespace MediaDownloader.Services.Notifications;

/// <summary>Sends messages through a user-configured Telegram bot (token + chat id).</summary>
public class TelegramNotifier : INotifier
{
    public string Name => "Telegram";

    private readonly IHttpClientFactory _httpClientFactory;

    public TelegramNotifier(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public bool IsEnabled(AppSettings s) =>
        s.TelegramEnabled &&
        !string.IsNullOrWhiteSpace(s.TelegramBotToken) &&
        !string.IsNullOrWhiteSpace(s.TelegramChatId);

    public async Task NotifyAsync(NotificationEvent evt, AppSettings s, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("notifications");
        var url = $"https://api.telegram.org/bot{s.TelegramBotToken}/sendMessage";
        var payload = new
        {
            chat_id = s.TelegramChatId,
            text = $"*{Escape(evt.Title)}*\n{Escape(evt.Message)}",
            parse_mode = "Markdown"
        };
        var response = await http.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string Escape(string text) => text.Replace("*", "\\*").Replace("_", "\\_");
}
