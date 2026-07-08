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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DownloadItem>()
            .HasOne(d => d.SeriesTask)
            .WithMany(s => s.Downloads)
            .HasForeignKey(d => d.SeriesTaskId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AppSettings>().Property(s => s.Id).ValueGeneratedNever();
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
}
