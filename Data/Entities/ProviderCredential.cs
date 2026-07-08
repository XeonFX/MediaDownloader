namespace MediaDownloader.Data.Entities;

/// <summary>
/// Stored account for a torrent search provider that requires login (one row per provider,
/// keyed by the provider's display name).
/// </summary>
public class ProviderCredential
{
    public int Id { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool IsComplete => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}
