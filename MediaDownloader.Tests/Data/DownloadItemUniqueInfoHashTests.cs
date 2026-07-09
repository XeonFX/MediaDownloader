using FluentAssertions;
using MediaDownloader.Data;
using MediaDownloader.Data.Entities;
using MediaDownloader.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Tests.Data;

/// <summary>
/// Verifies the unique index on DownloadItem.InfoHash (AppDbContext.OnModelCreating) is the real
/// backstop it's meant to be: two rows for the same torrent can never both exist, independent of
/// DownloadManager's own in-memory add lock.
/// </summary>
public class DownloadItemUniqueInfoHashTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public DownloadItemUniqueInfoHashTests()
    {
        var (factory, connection) = TestDb.CreateFactory();
        _factory = factory;
        _connection = connection;
    }

    public void Dispose() => _connection.Dispose();

    private static DownloadItem Download(string hash, string name = "Test") => new()
    {
        Name = name,
        InfoHash = hash,
        MagnetUri = $"magnet:?xt=urn:btih:{hash}",
        SavePath = "/tmp",
        Source = "Test"
    };

    [Fact]
    public async Task SavingTwoRowsWithSameInfoHash_Throws()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Downloads.Add(Download("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await db.SaveChangesAsync();

        db.Downloads.Add(Download("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SavingRowsWithDifferentInfoHashes_Succeeds()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Downloads.Add(Download("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        db.Downloads.Add(Download("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        await db.SaveChangesAsync();

        (await db.Downloads.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task MultipleRowsWithBlankInfoHash_AreAllowed()
    {
        // The unique index is partial (WHERE InfoHash <> '') specifically so any legacy/defensive
        // blank-hash rows don't collide with each other.
        await using var db = await _factory.CreateDbContextAsync();
        db.Downloads.Add(Download(""));
        db.Downloads.Add(Download(""));

        await db.SaveChangesAsync();

        (await db.Downloads.CountAsync()).Should().Be(2);
    }
}
