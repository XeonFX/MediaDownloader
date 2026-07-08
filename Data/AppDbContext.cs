using System.Data;
using MediaDownloader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DownloadItem> Downloads => Set<DownloadItem>();
    public DbSet<SeriesTask> SeriesTasks => Set<SeriesTask>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DownloadItem>()
            .HasOne(d => d.SeriesTask)
            .WithMany(s => s.Downloads)
            .HasForeignKey(d => d.SeriesTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AppSettings>().Property(s => s.Id).ValueGeneratedNever();

        modelBuilder.Entity<ProviderCredential>()
            .HasIndex(c => c.ProviderName)
            .IsUnique();
    }

    /// <summary>
    /// Patches columns added after a database was first created. The app uses EnsureCreated() rather
    /// than EF migrations, and EnsureCreated() only creates missing tables — it never alters an
    /// existing one — so new columns must be added by hand here. Idempotent and safe to run every start.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await EnsureColumnAsync("Settings", "PostDownloadAction", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync("Settings", "DisabledProviders", "TEXT NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync("Settings", "Language", "TEXT NOT NULL DEFAULT 'en'", ct);
        await EnsureColumnAsync("Downloads", "TorrentFilePath", "TEXT NULL", ct);

        // EnsureCreated() no-ops entirely once the database exists, so tables added later
        // must be created by hand too (mirrors the schema EF generates for fresh databases).
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS "ProviderCredentials" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ProviderCredentials" PRIMARY KEY AUTOINCREMENT,
                "ProviderName" TEXT NOT NULL,
                "Username" TEXT NOT NULL,
                "Password" TEXT NOT NULL
            )
            """, ct);
        await ExecuteAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProviderCredentials_ProviderName"
            ON "ProviderCredentials" ("ProviderName")
            """, ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        var connection = Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureColumnAsync(string table, string column, string definition, CancellationToken ct)
    {
        // table/column/definition are compile-time constants, never user input.
        var connection = Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0)
                return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        await alter.ExecuteNonQueryAsync(ct);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await Settings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
        {
            settings = new AppSettings { Id = 1 };
            Settings.Add(settings);
            await SaveChangesAsync(ct);
        }
        return settings;
    }

    /// <summary>Returns the stored account for a provider, or null when none has been saved.</summary>
    public Task<ProviderCredential?> GetProviderCredentialAsync(string providerName, CancellationToken ct = default) =>
        ProviderCredentials.FirstOrDefaultAsync(c => c.ProviderName == providerName, ct);
}
