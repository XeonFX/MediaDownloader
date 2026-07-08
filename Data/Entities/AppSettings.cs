namespace MediaDownloader.Data.Entities;

/// <summary>What the download engine does once a torrent has finished downloading.</summary>
public enum PostDownloadAction
{
    /// <summary>Stop the torrent as soon as it completes (no seeding). Default.</summary>
    StopSeeding = 0,

    /// <summary>Keep seeding indefinitely until the user removes or pauses the download.</summary>
    KeepSeeding = 1
}

/// <summary>Single-row settings table (Id is always 1).</summary>
public class AppSettings
{
    public int Id { get; set; } = 1;

    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MediaDownloader");

    /// <summary>What to do when a download finishes. Defaults to stopping (no seeding).</summary>
    public PostDownloadAction PostDownloadAction { get; set; } = PostDownloadAction.StopSeeding;

    /// <summary>Comma-separated names of search providers the user has turned off. Empty = all enabled.</summary>
    public string DisabledProviders { get; set; } = string.Empty;

    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;

    // Email (SMTP)
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string EmailFrom { get; set; } = string.Empty;
    public string EmailTo { get; set; } = string.Empty;

    // Desktop (browser) notifications
    public bool DesktopEnabled { get; set; } = true;

    // Push notifications via ntfy
    public bool PushEnabled { get; set; }
    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = string.Empty;

    // Telegram bot
    public bool TelegramEnabled { get; set; }
    public string TelegramBotToken { get; set; } = string.Empty;
    public string TelegramChatId { get; set; } = string.Empty;

    public HashSet<string> GetDisabledProviders() =>
        DisabledProviders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsProviderEnabled(string name) => !GetDisabledProviders().Contains(name);

    public void SetProviderEnabled(string name, bool enabled)
    {
        var set = GetDisabledProviders();
        if (enabled) set.Remove(name);
        else set.Add(name);
        DisabledProviders = string.Join(",", set);
    }
}
