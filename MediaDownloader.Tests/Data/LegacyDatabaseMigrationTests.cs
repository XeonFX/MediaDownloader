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
/// Verifies AppDbContext.MigrateAsync's upgrade path for a real existing install: a database
/// created by the pre-migrations EnsureCreated()+manual-ALTER-TABLE scheme this app used through
/// v1.0.2, with no __EFMigrationsHistory table and missing the columns/index added in the same
/// release that introduced migrations (NameIsPlaceholder, the InfoHash unique index). This is the
/// highest-risk path in the schema-management rewrite — it runs against every real user's database
/// on their next auto-update, so it's covered against a hand-built legacy schema rather than trusted
/// to work by inspection.
/// </summary>
public class LegacyDatabaseMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"md-legacy-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        File.Delete(_dbPath + "-shm");
        File.Delete(_dbPath + "-wal");
    }

    /// <summary>
    /// Hand-builds the schema exactly as a real v1.0.2 install would have it: every table/column
    /// the old EnsureSchemaAsync had already patched in by that release, but missing NameIsPlaceholder
    /// and the InfoHash unique index (both new in the same release as the migrations switch), and
    /// seeded with data including a duplicate-InfoHash pair to exercise the dedup-before-index step.
    /// </summary>
    private async Task SeedLegacySchemaAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE "SeriesTasks" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_SeriesTasks" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "Query" TEXT NOT NULL,
                    "Provider" TEXT NULL,
                    "TitleFilter" TEXT NULL,
                    "Season" INTEGER NULL,
                    "StartEpisode" INTEGER NOT NULL,
                    "EndEpisode" INTEGER NULL,
                    "DownloadFolder" TEXT NULL,
                    "LastDownloadedEpisode" INTEGER NOT NULL,
                    "CheckIntervalMinutes" INTEGER NOT NULL,
                    "Enabled" INTEGER NOT NULL,
                    "LastCheckedAt" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );

                CREATE TABLE "Settings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
                    "DownloadFolder" TEXT NOT NULL,
                    "PostDownloadAction" INTEGER NOT NULL DEFAULT 0,
                    "DisabledProviders" TEXT NOT NULL DEFAULT '',
                    "Language" TEXT NOT NULL DEFAULT 'en',
                    "NotifyOnStart" INTEGER NOT NULL,
                    "NotifyOnComplete" INTEGER NOT NULL,
                    "EmailEnabled" INTEGER NOT NULL,
                    "SmtpHost" TEXT NOT NULL,
                    "SmtpPort" INTEGER NOT NULL,
                    "SmtpUseSsl" INTEGER NOT NULL,
                    "SmtpUsername" TEXT NOT NULL,
                    "SmtpPassword" TEXT NOT NULL,
                    "EmailFrom" TEXT NOT NULL,
                    "EmailTo" TEXT NOT NULL,
                    "DesktopEnabled" INTEGER NOT NULL,
                    "PushEnabled" INTEGER NOT NULL,
                    "NtfyServer" TEXT NOT NULL,
                    "NtfyTopic" TEXT NOT NULL,
                    "TelegramEnabled" INTEGER NOT NULL,
                    "TelegramBotToken" TEXT NOT NULL,
                    "TelegramChatId" TEXT NOT NULL
                );

                CREATE TABLE "Downloads" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Downloads" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "MagnetUri" TEXT NOT NULL,
                    "TorrentFilePath" TEXT NULL,
                    "InfoHash" TEXT NOT NULL,
                    "SavePath" TEXT NOT NULL,
                    "Source" TEXT NOT NULL,
                    "Status" INTEGER NOT NULL,
                    "Progress" REAL NOT NULL,
                    "TotalBytes" INTEGER NOT NULL,
                    "AddedAt" TEXT NOT NULL,
                    "CompletedAt" TEXT NULL,
                    "Error" TEXT NULL,
                    "StartNotificationSent" INTEGER NOT NULL,
                    "CompleteNotificationSent" INTEGER NOT NULL,
                    "SeriesTaskId" INTEGER NULL,
                    CONSTRAINT "FK_Downloads_SeriesTasks_SeriesTaskId" FOREIGN KEY ("SeriesTaskId")
                        REFERENCES "SeriesTasks" ("Id") ON DELETE SET NULL
                );

                CREATE TABLE "ProviderCredentials" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ProviderCredentials" PRIMARY KEY AUTOINCREMENT,
                    "ProviderName" TEXT NOT NULL,
                    "Username" TEXT NOT NULL,
                    "Password" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_ProviderCredentials_ProviderName" ON "ProviderCredentials" ("ProviderName");

                INSERT INTO "Settings" ("Id", "DownloadFolder", "NotifyOnStart", "NotifyOnComplete",
                    "EmailEnabled", "SmtpHost", "SmtpPort", "SmtpUseSsl", "SmtpUsername", "SmtpPassword",
                    "EmailFrom", "EmailTo", "DesktopEnabled", "PushEnabled", "NtfyServer", "NtfyTopic",
                    "TelegramEnabled", "TelegramBotToken", "TelegramChatId")
                VALUES (1, '/data/downloads', 1, 1, 0, '', 587, 1, '', '', '', '', 1, 0, 'https://ntfy.sh', '', 0, '', '');

                INSERT INTO "Downloads" ("Name", "MagnetUri", "InfoHash", "SavePath", "Source", "Status",
                    "Progress", "TotalBytes", "AddedAt", "StartNotificationSent", "CompleteNotificationSent")
                VALUES ('Existing Download', 'magnet:?xt=urn:btih:cccccccccccccccccccccccccccccccccccccccc',
                    'cccccccccccccccccccccccccccccccccccccccc', '/data/downloads', 'Test', 5, 100, 1000,
                    '2026-01-01T00:00:00Z', 1, 1);

                -- A duplicate pair from the pre-fix race: same InfoHash, two rows.
                INSERT INTO "Downloads" ("Name", "MagnetUri", "InfoHash", "SavePath", "Source", "Status",
                    "Progress", "TotalBytes", "AddedAt", "StartNotificationSent", "CompleteNotificationSent")
                VALUES ('Duplicate A', 'magnet:?xt=urn:btih:dddddddddddddddddddddddddddddddddddddddd',
                    'dddddddddddddddddddddddddddddddddddddddd', '/data/downloads', 'Test', 2, 10, 1000,
                    '2026-01-02T00:00:00Z', 1, 0);
                INSERT INTO "Downloads" ("Name", "MagnetUri", "InfoHash", "SavePath", "Source", "Status",
                    "Progress", "TotalBytes", "AddedAt", "StartNotificationSent", "CompleteNotificationSent")
                VALUES ('Duplicate B', 'magnet:?xt=urn:btih:dddddddddddddddddddddddddddddddddddddddd',
                    'dddddddddddddddddddddddddddddddddddddddd', '/data/downloads', 'Test', 2, 10, 1000,
                    '2026-01-03T00:00:00Z', 1, 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private IDbContextFactory<AppDbContext> BuildFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<SecretProtector>();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();
    }

    [Fact]
    public async Task MigrateAsync_UpgradesLegacyDatabase_WithoutLosingData()
    {
        await SeedLegacySchemaAsync();
        var factory = BuildFactory();

        await using (var db = await factory.CreateDbContextAsync())
            await db.MigrateAsync();

        await using var verify = await factory.CreateDbContextAsync();

        // The pre-existing row survived the upgrade untouched.
        var kept = await verify.Downloads.SingleAsync(d => d.Name == "Existing Download");
        kept.InfoHash.Should().Be("cccccccccccccccccccccccccccccccccccccccc");
        kept.NameIsPlaceholder.Should().BeFalse(); // new column defaults sanely for pre-existing rows

        // The duplicate pair was deduped down to one row (the most recently added).
        var duplicates = await verify.Downloads
            .Where(d => d.InfoHash == "dddddddddddddddddddddddddddddddddddddddd")
            .ToListAsync();
        duplicates.Should().ContainSingle().Which.Name.Should().Be("Duplicate B");

        // Existing settings values weren't clobbered.
        var settings = await verify.GetSettingsAsync();
        settings.DownloadFolder.Should().Be("/data/downloads");
    }

    [Fact]
    public async Task MigrateAsync_RecordsInitialCreateAsApplied()
    {
        await SeedLegacySchemaAsync();
        var factory = BuildFactory();

        await using (var db = await factory.CreateDbContextAsync())
            await db.MigrateAsync();

        await using var verify = await factory.CreateDbContextAsync();
        var applied = await verify.Database.GetAppliedMigrationsAsync();
        applied.Should().ContainSingle(m => m.EndsWith("_InitialCreate"));

        var pending = await verify.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task MigrateAsync_UniqueIndexIsEnforced_AfterUpgrade()
    {
        await SeedLegacySchemaAsync();
        var factory = BuildFactory();

        await using (var db = await factory.CreateDbContextAsync())
            await db.MigrateAsync();

        await using var verify = await factory.CreateDbContextAsync();
        verify.Downloads.Add(new DownloadItem
        {
            Name = "New",
            // Colliding with the row that survived the dedup above.
            InfoHash = "dddddddddddddddddddddddddddddddddddddddd",
            MagnetUri = "magnet:?xt=urn:btih:dddddddddddddddddddddddddddddddddddddddd",
            SavePath = "/data/downloads",
            Source = "Test"
        });

        var act = () => verify.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task MigrateAsync_IsIdempotent_WhenCalledAgain()
    {
        await SeedLegacySchemaAsync();
        var factory = BuildFactory();

        await using (var db = await factory.CreateDbContextAsync())
            await db.MigrateAsync();

        // Simulates the next app start against an already-upgraded database.
        await using var second = await factory.CreateDbContextAsync();
        var act = () => second.MigrateAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MigrateAsync_CreatesFullSchema_ForABrandNewDatabase()
    {
        // No SeedLegacySchemaAsync call — the file doesn't exist yet at all.
        var factory = BuildFactory();

        await using (var db = await factory.CreateDbContextAsync())
            await db.MigrateAsync();

        await using var verify = await factory.CreateDbContextAsync();
        (await verify.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        var settings = await verify.GetSettingsAsync();
        settings.Id.Should().Be(1);
    }
}
