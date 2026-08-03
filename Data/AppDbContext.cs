using System.ComponentModel.DataAnnotations;
using System.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MediaDownloader.Data;

public class AppDbContext : DbContext
{
    private readonly SecretProtector _protector;

    public AppDbContext(DbContextOptions<AppDbContext> options, SecretProtector protector) : base(options)
    {
        _protector = protector;
    }

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

        // Two DownloadItem rows for the same torrent would attach it to the engine twice. The
        // check-then-insert in DownloadManager also guards against this at the app level (see
        // DownloadManager's per-hash add lock), but a DB-level constraint is the real backstop —
        // partial so legacy/defensive blank hashes never collide with each other.
        modelBuilder.Entity<DownloadItem>()
            .HasIndex(d => d.InfoHash)
            .IsUnique()
            .HasFilter("\"InfoHash\" <> ''");

        modelBuilder.Entity<ProviderCredential>()
            .HasIndex(c => c.ProviderName)
            .IsUnique();

        // Encrypt secrets at rest. SecretProtector is a singleton, so every AppDbContext instance
        // (there are many — one per IDbContextFactory.CreateDbContextAsync call) shares the same
        // underlying key ring regardless of which instance's OnModelCreating EF actually ran (EF
        // caches the compiled model across instances by default).
        var secretConverter = new ValueConverter<string, string>(
            plaintext => _protector.Protect(plaintext),
            stored => _protector.Unprotect(stored));

        modelBuilder.Entity<AppSettings>().Property(s => s.SmtpPassword).HasConversion(secretConverter);
        modelBuilder.Entity<AppSettings>().Property(s => s.TelegramBotToken).HasConversion(secretConverter);
        modelBuilder.Entity<AppSettings>().Property(s => s.AgentApiToken).HasConversion(secretConverter);
        modelBuilder.Entity<ProviderCredential>().Property(c => c.Password).HasConversion(secretConverter);
    }

    /// <summary>
    /// Brings the database up to date with the current model via real EF Core migrations
    /// (<c>Data/Migrations</c>), replacing the hand-written ALTER-TABLE patching this app used
    /// through v1.0.2. Safe to call on three different starting points:
    /// <list type="bullet">
    /// <item>A brand-new database (no file, or an empty one): <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/>
    /// creates the migrations-history table and runs every migration's Up() from scratch.</item>
    /// <item>A database already on migrations (this method having run before): same call just
    /// applies whatever migrations are new since last start.</item>
    /// <item>An existing install's database created by the old EnsureCreated()+manual-patch scheme
    /// (no history table, but its tables already exist): running InitialCreate's Up() here would
    /// fail with "table already exists". Instead this brings it up to exactly the schema
    /// InitialCreate describes using the same idempotent statements the old scheme used, then
    /// records InitialCreate as already applied via EF's own <see cref="IHistoryRepository"/> —
    /// the same mechanism EF's own docs describe for adopting migrations on a database that
    /// wasn't created by migrations in the first place — before letting MigrateAsync take over
    /// for anything newer.</item>
    /// </list>
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var history = this.GetService<IHistoryRepository>();
        if (!await history.ExistsAsync(ct) && await TableExistsAsync("Settings", ct))
        {
            await PatchLegacySchemaAsync(ct);
            await StampInitialMigrationAppliedAsync(history, ct);
        }

        await Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Brings a pre-migrations database's schema up to exactly what the InitialCreate migration
    /// describes, so it's safe to record that migration as already applied instead of running it.
    /// Every statement here is defensively idempotent (existing installs will already have most of
    /// this from the old EnsureSchemaAsync patches; a fresh-but-somehow-history-less DB would have
    /// none of it) — safe to run unconditionally on this path.
    /// </summary>
    private async Task PatchLegacySchemaAsync(CancellationToken ct)
    {
        await EnsureColumnAsync("Settings", "PostDownloadAction", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync("Settings", "DisabledProviders", "TEXT NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync("Settings", "Language", "TEXT NOT NULL DEFAULT 'en'", ct);
        await EnsureColumnAsync("Downloads", "TorrentFilePath", "TEXT NULL", ct);
        await EnsureColumnAsync("Downloads", "NameIsPlaceholder", "INTEGER NOT NULL DEFAULT 0", ct);

        // A pre-existing DB may have duplicate InfoHash rows from before the add path was guarded
        // against the check-then-insert race (a manual add and a series-monitor add landing for the
        // same torrent at the same instant). Keep the most recent row per hash so the unique index
        // InitialCreate declares can actually be created below — older duplicates are just
        // redundant DB rows, not lost downloads (the underlying torrent content already lives
        // under one save path either way).
        await ExecuteAsync("""
            DELETE FROM "Downloads"
            WHERE "InfoHash" <> ''
            AND "Id" NOT IN (
                SELECT MAX("Id") FROM "Downloads" WHERE "InfoHash" <> '' GROUP BY "InfoHash"
            )
            """, ct);
        await ExecuteAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Downloads_InfoHash"
            ON "Downloads" ("InfoHash")
            WHERE "InfoHash" <> ''
            """, ct);
        await ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS "IX_Downloads_SeriesTaskId"
            ON "Downloads" ("SeriesTaskId")
            """, ct);

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

    /// <summary>
    /// Records the InitialCreate migration as already applied without running its Up() — used only
    /// once, for a database <see cref="PatchLegacySchemaAsync"/> has just confirmed already matches
    /// that migration's target schema. Uses EF's own <see cref="IHistoryRepository"/> to generate
    /// the create/insert SQL rather than hand-rolling the history table format.
    /// </summary>
    private async Task StampInitialMigrationAppliedAsync(IHistoryRepository history, CancellationToken ct)
    {
        var migrationsAssembly = this.GetService<IMigrationsAssembly>();
        var initialCreateId = migrationsAssembly.Migrations.Keys
            .First(id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));

        await ExecuteAsync(history.GetCreateIfNotExistsScript(), ct);
        await ExecuteAsync(history.GetInsertScript(new HistoryRow(initialCreateId, ProductInfo.GetVersion())), ct);
    }

    private async Task<bool> TableExistsAsync(string table, CancellationToken ct)
    {
        var connection = Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table";
        var param = cmd.CreateParameter();
        param.ParameterName = "$table";
        param.Value = table;
        cmd.Parameters.Add(param);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
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
        if (settings is not null)
            return settings;

        settings = new AppSettings { Id = 1 };
        Settings.Add(settings);
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (!ct.IsCancellationRequested)
        {
            // Another concurrent caller (e.g. a second startup path) won the insert race — detach
            // our copy and read back the row it created instead of failing this call.
            Entry(settings).State = EntityState.Detached;
            settings = await Settings.FirstAsync(s => s.Id == 1, ct);
        }
        return settings;
    }

    /// <summary>Returns the stored account for a provider, or null when none has been saved.</summary>
    public Task<ProviderCredential?> GetProviderCredentialAsync(string providerName, CancellationToken ct = default) =>
        ProviderCredentials.FirstOrDefaultAsync(c => c.ProviderName == providerName, ct);

    // EF Core doesn't enforce DataAnnotations on SaveChanges by itself (that's an ASP.NET MVC
    // model-binding behavior) — without this override, [Range]/[EmailAddress] etc. on entities
    // like AppSettings are just documentation. Validating every added/modified entity here means
    // any write path (not just one UI page remembering to check) gets the same guarantee.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTrackedEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateTrackedEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateTrackedEntities()
    {
        var errors = new List<string>();
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(entry.Entity, new ValidationContext(entry.Entity), results, validateAllProperties: true);
            errors.AddRange(results.Select(r => r.MemberNames.Any()
                ? $"{string.Join(", ", r.MemberNames)}: {r.ErrorMessage ?? "Invalid value"}"
                : r.ErrorMessage ?? "Invalid value"));
        }

        if (errors.Count > 0)
            throw new ValidationException(string.Join(" ", errors));
    }
}
