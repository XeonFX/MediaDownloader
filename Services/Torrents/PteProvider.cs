using System.Net;
using System.Text.Json;
using MediaDownloader.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Searches pte.nu (PolishTracker), a private tracker that requires an account. Login is a plain
/// form POST that sets long-lived session cookies; search goes through the site's JSON API. Being
/// private, PTE serves .torrent files (with per-user announce keys) instead of magnet links, so
/// this provider also implements <see cref="ITorrentFileSource"/>.
/// </summary>
public class PteProvider : ITorrentSearchProvider, ITorrentFileSource, ITorrentDetailsProvider, IDisposable
{
    public const string ProviderName = "PTE";
    public string Name => ProviderName;
    public bool RequiresCredentials => true;

    private const string BaseUrl = "https://pte.nu";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<PteProvider> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private DateTime _lastLoginAt = DateTime.MinValue;

    public PteProvider(IDbContextFactory<AppDbContext> dbFactory, ILogger<PteProvider> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        // A dedicated client (not the pooled factory one) because the session lives in cookies.
        // Redirects stay manual: a 302 to /login is how we detect an expired session.
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader/1.0");
    }

    public async Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Note: the API rejects the website's cat=all — omitting cat searches all categories.
        var url = $"{BaseUrl}/api/v1/torrents?search={Uri.EscapeDataString(query)}&tpage=1&pageSize=100";
        using var response = await SendAuthenticatedAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<TorrentSearchResult>();
        if (!doc.RootElement.TryGetProperty("torrents", out var torrents))
            return results;

        foreach (var item in torrents.EnumerateArray())
        {
            var id = item.GetProperty("id").GetInt64();
            var name = item.GetProperty("name").GetString() ?? "";
            // size comes as a decimal string, e.g. "2126677252"
            var sizeText = item.TryGetProperty("size", out var s) ? s.GetString() : null;

            results.Add(new TorrentSearchResult
            {
                Title = name,
                // The API doesn't expose info hashes; a per-torrent placeholder keeps dedup working.
                InfoHash = $"pte-{id}",
                SizeBytes = long.TryParse(sizeText, out var size) ? size : 0,
                Seeders = item.TryGetProperty("seeders", out var se) ? se.GetInt32() : 0,
                Leechers = item.TryGetProperty("leechers", out var le) ? le.GetInt32() : 0,
                PublishedAt = item.TryGetProperty("added", out var a) && a.TryGetDateTime(out var added)
                    ? added.ToUniversalTime()
                    : null,
                Source = Name,
                DetailsUrl = $"{BaseUrl}/torrents/{id}",
                TorrentFileUrl = $"{BaseUrl}/download/{id}"
            });
        }
        return results;
    }

    /// <summary>
    /// Fetches the description for one result from PTE's single-torrent API. The search listing
    /// never includes it, so this is only ever called on demand (info dialog / direct download).
    /// </summary>
    public async Task<TorrentDetails> GetDetailsAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        var id = result.TorrentFileUrl?.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(id))
            return new TorrentDetails();

        using var response = await SendAuthenticatedAsync($"{BaseUrl}/api/v1/torrent/{id}", ct);
        if (!response.IsSuccessStatusCode)
            return new TorrentDetails();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        // Movie/TV/game releases have a "description" synopsis; other releases (magazines, apps,
        // scene rips without metadata) instead carry release notes in "nfoparsed" — fall back to it
        // so non-media torrents still show something useful.
        var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(description))
            description = doc.RootElement.TryGetProperty("nfoparsed", out var nfo) ? nfo.GetString() : null;
        return new TorrentDetails { Description = description };
    }

    public async Task<byte[]> DownloadTorrentFileAsync(TorrentSearchResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(result.TorrentFileUrl))
            throw new InvalidOperationException("Result has no torrent file URL");

        using var response = await SendAuthenticatedAsync(result.TorrentFileUrl, ct);

        // Errors (e.g. "downloading disabled for your account") come back as JSON with a message.
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(ExtractApiMessage(body) ?? $"PTE refused the download ({(int)response.StatusCode})");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Sends a GET, transparently (re-)logging in when the session is missing or expired.</summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        if (!IsLoginRedirect(response) && response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        await LoginAsync(ct);

        response = await _http.GetAsync(url, ct);
        if (IsLoginRedirect(response))
        {
            response.Dispose();
            throw new InvalidOperationException("PTE session could not be established — check the credentials in Settings.");
        }
        return response;
    }

    private static bool IsLoginRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and < 400 &&
        response.Headers.Location?.OriginalString.Contains("login", StringComparison.OrdinalIgnoreCase) == true;

    private async Task LoginAsync(CancellationToken ct)
    {
        await _loginGate.WaitAsync(ct);
        try
        {
            // Another caller may have just logged in while we waited for the gate.
            if (DateTime.UtcNow - _lastLoginAt < TimeSpan.FromSeconds(10))
                return;

            var credential = await GetCredentialAsync(ct)
                ?? throw new InvalidOperationException("PTE requires an account — save credentials in Settings → Search sources.");

            using var response = await _http.PostAsync($"{BaseUrl}/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = credential.Username,
                ["pass"] = credential.Password,
                ["totp"] = ""
            }), ct);

            // Success is a 302 to "/"; bad credentials bounce back to /login.
            if (IsLoginRedirect(response) || (int)response.StatusCode is not (>= 300 and < 400))
                throw new InvalidOperationException("PTE login failed — check the e-mail and password in Settings.");

            _lastLoginAt = DateTime.UtcNow;
            _logger.LogInformation("Logged in to PTE as {User}", credential.Username);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private async Task<Data.Entities.ProviderCredential?> GetCredentialAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var credential = await db.GetProviderCredentialAsync(Name, ct);
        return credential is { IsComplete: true } ? credential : null;
    }

    private static string? ExtractApiMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _loginGate.Dispose();
    }
}
