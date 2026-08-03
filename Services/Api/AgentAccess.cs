using System.Security.Cryptography;
using System.Text;
using MediaDownloader.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Api;

/// <summary>
/// The agent API's on/off state and bearer token, cached so the auth check doesn't hit SQLite on
/// every request (an MCP client can be chatty). The Settings page calls <see cref="Invalidate"/>
/// after saving; the short TTL is only a backstop for anything that changes the row another way.
/// </summary>
public class AgentAccess
{
    /// <summary>Longest a settings change can take to be noticed if nothing calls <see cref="Invalidate"/>.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    public record Snapshot(bool Enabled, bool AllowRemote, string Token);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _cached;
    private DateTime _cachedAt;

    public AgentAccess(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>Forces the next read to go back to the database.</summary>
    public void Invalidate() => _cached = null;

    public async Task<Snapshot> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached && DateTime.UtcNow - _cachedAt < CacheLifetime)
            return cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } current && DateTime.UtcNow - _cachedAt < CacheLifetime)
                return current;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.GetSettingsAsync(ct);
            _cached = new Snapshot(settings.AgentApiEnabled, settings.AgentApiAllowRemote, settings.AgentApiToken);
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns the current token, generating and persisting one if there isn't a token yet. Called
    /// when the feature is switched on and when the user asks for a new token.
    /// </summary>
    public async Task<string> EnsureTokenAsync(bool regenerate = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await db.GetSettingsAsync(ct);

        if (regenerate || string.IsNullOrEmpty(settings.AgentApiToken))
        {
            settings.AgentApiToken = GenerateToken();
            await db.SaveChangesAsync(ct);
            Invalidate();
        }

        return settings.AgentApiToken;
    }

    /// <summary>256 bits of randomness, base64url so it survives being pasted into a shell or a JSON config.</summary>
    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Compares a presented token against the configured one without leaking, through timing, how
    /// much of a guess was correct.
    /// </summary>
    public static bool TokenMatches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}
