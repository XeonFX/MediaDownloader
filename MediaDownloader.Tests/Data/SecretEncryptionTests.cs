using FluentAssertions;
using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaDownloader.Tests.Data;

/// <summary>
/// Verifies that secrets (provider passwords, SMTP password, Telegram bot token) are encrypted
/// before they reach the database, and that a database upgraded from a version that stored them as
/// plaintext still loads correctly.
/// </summary>
public class SecretEncryptionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SecretEncryptionTests()
    {
        // A single open in-memory connection kept alive for the test's lifetime — SQLite's
        // ":memory:" database is dropped once the last connection to it closes.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<SecretProtector>();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));
        var provider = services.BuildServiceProvider();

        using (var db = provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext())
            db.Database.EnsureCreated();

        _factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    }

    [Fact]
    public async Task SmtpPassword_IsNotStoredAsPlaintext()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var settings = await db.GetSettingsAsync();
            settings.SmtpPassword = "correct horse battery staple";
            await db.SaveChangesAsync();
        }

        var raw = await ReadRawColumnAsync("Settings", "SmtpPassword");
        raw.Should().NotBe("correct horse battery staple");
        raw.Should().StartWith("dp1:");
    }

    [Fact]
    public async Task SmtpPassword_RoundTripsThroughEfCore()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var settings = await db.GetSettingsAsync();
            settings.SmtpPassword = "correct horse battery staple";
            await db.SaveChangesAsync();
        }

        await using var reload = await _factory.CreateDbContextAsync();
        var reloaded = await reload.GetSettingsAsync();
        reloaded.SmtpPassword.Should().Be("correct horse battery staple");
    }

    [Fact]
    public async Task AgentApiToken_IsEncryptedAndRoundTrips()
    {
        const string token = "agent-api-secret-token";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var settings = await db.GetSettingsAsync();
            settings.AgentApiToken = token;
            await db.SaveChangesAsync();
        }

        (await ReadRawColumnAsync("Settings", "AgentApiToken")).Should().StartWith("dp1:");

        await using var reload = await _factory.CreateDbContextAsync();
        (await reload.GetSettingsAsync()).AgentApiToken.Should().Be(token);
    }

    [Fact]
    public async Task ProviderCredentialPassword_IsEncryptedAndRoundTrips()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderCredentials.Add(new ProviderCredential
            {
                ProviderName = "PTE",
                Username = "alice",
                Password = "hunter2"
            });
            await db.SaveChangesAsync();
        }

        var raw = await ReadRawColumnAsync("ProviderCredentials", "Password");
        raw.Should().NotBe("hunter2");

        await using var reload = await _factory.CreateDbContextAsync();
        var reloaded = await reload.GetProviderCredentialAsync("PTE");
        reloaded.Should().NotBeNull();
        reloaded!.Password.Should().Be("hunter2");
    }

    [Fact]
    public async Task LegacyPlaintextSecret_StillLoadsCorrectly_AndIsEncryptedOnNextSave()
    {
        // Simulate a database from before encryption was added: write the column directly via ADO.NET,
        // bypassing the EF value converter entirely.
        await using (var raw = new SqliteCommand(
                         "UPDATE Settings SET SmtpPassword = @p WHERE Id = 1", _connection))
        {
            raw.Parameters.AddWithValue("@p", "legacy-plaintext-password");
            if (raw.ExecuteNonQuery() == 0)
            {
                // No settings row yet — create it first via EF, then overwrite the column with plaintext.
                await using var db = await _factory.CreateDbContextAsync();
                await db.GetSettingsAsync();
                raw.ExecuteNonQuery();
            }
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var settings = await db.GetSettingsAsync();
            settings.SmtpPassword.Should().Be("legacy-plaintext-password");

            // Touching and saving should upgrade the stored value to encrypted form.
            db.Settings.Update(settings);
            await db.SaveChangesAsync();
        }

        var raw2 = await ReadRawColumnAsync("Settings", "SmtpPassword");
        raw2.Should().StartWith("dp1:");
    }

    private async Task<string?> ReadRawColumnAsync(string table, string column)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT \"{column}\" FROM \"{table}\" LIMIT 1";
        return (string?)await cmd.ExecuteScalarAsync();
    }

    public void Dispose() => _connection.Dispose();
}
