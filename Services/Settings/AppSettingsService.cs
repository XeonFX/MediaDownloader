using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Settings;

/// <summary>
/// Data-access layer for app settings and provider credentials, extracted out of Settings.razor so
/// the page focuses on presentation and this logic can be exercised without a full component render.
/// </summary>
public class AppSettingsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AppSettingsService(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.GetSettingsAsync(ct);
    }

    public async Task<List<ProviderCredential>> GetCredentialsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ProviderCredentials.ToListAsync(ct);
    }

    /// <summary>
    /// Saves settings. Throws <see cref="System.ComponentModel.DataAnnotations.ValidationException"/>
    /// if they fail AppSettings' validation rules (e.g. an out-of-range SMTP port or a malformed
    /// email address) — callers should catch that and surface it rather than let it propagate.
    /// </summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Settings.Update(settings);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveCredentialAsync(ProviderCredential credential, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (credential.Id == 0)
            db.ProviderCredentials.Add(credential);
        else
            db.ProviderCredentials.Update(credential);
        await db.SaveChangesAsync(ct);
    }
}
