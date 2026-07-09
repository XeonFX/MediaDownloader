using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MediaDownloader.Services.Security;

/// <summary>
/// Encrypts sensitive settings (provider account passwords, SMTP password, Telegram bot token)
/// before they reach the SQLite database, and decrypts them on read — so a copy of the DB file
/// alone isn't enough to recover credentials. Wired into <see cref="Data.AppDbContext"/> via an EF
/// Core value converter, so callers keep reading/writing plain strings.
/// Keys are persisted under the app's data directory (see Program.cs) so they survive restarts and
/// travel with the DB in a backup, but only decrypt on the machine that generated them.
/// </summary>
public class SecretProtector
{
    // Marks a value as encrypted by this class. An existing database from before this feature
    // shipped has plaintext in these columns; Unprotect leaves anything without this prefix
    // untouched so those old values still load correctly, and they get encrypted on next save.
    private const string Prefix = "dp1:";

    private readonly IDataProtector _protector;
    private readonly ILogger<SecretProtector> _logger;

    public SecretProtector(IDataProtectionProvider provider, ILogger<SecretProtector> logger)
    {
        _protector = provider.CreateProtector("MediaDownloader.Secrets.v1");
        _logger = logger;
    }

    public string Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? string.Empty : Prefix + _protector.Protect(plaintext);

    public string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        try
        {
            return _protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Could not decrypt a stored secret (keys missing or changed) — treating it as unset");
            return string.Empty;
        }
    }
}
