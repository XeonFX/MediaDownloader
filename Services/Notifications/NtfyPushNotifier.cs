using System.Text;
using MediaDownloader.Data.Entities;

namespace MediaDownloader.Services.Notifications;

/// <summary>
/// Push notifications via ntfy (https://ntfy.sh) — subscribe to the topic in the
/// ntfy mobile/desktop app to receive pushes on any device.
/// </summary>
public class NtfyPushNotifier : INotifier
{
    public string Name => "Push (ntfy)";

    private readonly IHttpClientFactory _httpClientFactory;

    public NtfyPushNotifier(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public bool IsEnabled(AppSettings s) =>
        s.PushEnabled &&
        !string.IsNullOrWhiteSpace(s.NtfyServer) &&
        !string.IsNullOrWhiteSpace(s.NtfyTopic);

    public async Task NotifyAsync(NotificationEvent evt, AppSettings s, CancellationToken ct = default)
    {
        var http = _httpClientFactory.CreateClient("notifications");
        var url = $"{s.NtfyServer.TrimEnd('/')}/{s.NtfyTopic}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(evt.Message, Encoding.UTF8)
        };
        request.Headers.Add("Title", evt.Title);
        request.Headers.Add("Tags", evt.Kind == NotificationKind.DownloadCompleted ? "white_check_mark" : "arrow_down");
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
