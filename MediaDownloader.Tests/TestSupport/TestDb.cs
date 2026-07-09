using MediaDownloader.Data;
using MediaDownloader.Services.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaDownloader.Tests.TestSupport;

/// <summary>
/// Builds an AppDbContext factory backed by a fresh in-memory SQLite database, for tests that
/// need real EF Core behavior (encryption converters, validation) without a file on disk. Callers
/// own disposing the returned connection — the in-memory database is dropped once it closes.
/// </summary>
internal static class TestDb
{
    public static (IDbContextFactory<AppDbContext> Factory, SqliteConnection Connection) CreateFactory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<SecretProtector>();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        return (factory, connection);
    }
}
