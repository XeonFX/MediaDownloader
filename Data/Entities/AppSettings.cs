using System.ComponentModel.DataAnnotations;

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
public class AppSettings : IValidatableObject
{
    public int Id { get; set; } = 1;

    [Required(AllowEmptyStrings = false, ErrorMessage = "Download folder is required")]
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MediaDownloader");

    /// <summary>What to do when a download finishes. Defaults to stopping (no seeding).</summary>
    public PostDownloadAction PostDownloadAction { get; set; } = PostDownloadAction.StopSeeding;

    /// <summary>Comma-separated names of search providers the user has turned off. Empty = all enabled.</summary>
    public string DisabledProviders { get; set; } = string.Empty;

    /// <summary>UI language code, matching a JSON file in Resources/i18n (e.g. "en", "pl").</summary>
    public string Language { get; set; } = "en";

    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;

    // Email (SMTP)
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;

    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535")]
    public int SmtpPort { get; set; } = 587;

    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;

    // Both are optional (EmailFrom falls back to SmtpUsername; EmailTo just means the feature is
    // unconfigured) — validated via IValidatableObject below instead of [EmailAddress] directly,
    // since that attribute rejects an empty string rather than treating it as "not set".
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrEmpty(EmailFrom) && !IsValidEmail(EmailFrom))
            yield return new ValidationResult("From address must be a valid email address", new[] { nameof(EmailFrom) });
        if (!string.IsNullOrEmpty(EmailTo) && !IsValidEmail(EmailTo))
            yield return new ValidationResult("To address must be a valid email address", new[] { nameof(EmailTo) });
    }

    private static bool IsValidEmail(string email) => new EmailAddressAttribute().IsValid(email);
}
