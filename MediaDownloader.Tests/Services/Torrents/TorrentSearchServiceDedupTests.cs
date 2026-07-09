using FluentAssertions;
using MediaDownloader.Data;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaDownloader.Tests.Services.Torrents;

/// <summary>
/// Covers <see cref="TorrentSearchService.SearchAsync"/>'s cross-provider de-duplication: results
/// that share a real info hash collapse to the healthiest row, but results still awaiting detail
/// resolution (blank hash) must not be merged together — grouping those by hash would collapse
/// unrelated torrents into one and silently drop all but the highest-seeded.
/// </summary>
public class TorrentSearchServiceDedupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public TorrentSearchServiceDedupTests()
    {
        (_factory, _connection) = TestDb.CreateFactory();
    }

    public void Dispose() => _connection.Dispose();

    private TorrentSearchService CreateService(params ITorrentSearchProvider[] providers) =>
        new(providers, _factory, NullLogger<TorrentSearchService>.Instance);

    [Fact]
    public async Task SearchAsync_KeepsHighestSeededRow_ForSharedInfoHash()
    {
        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        var a = new FakeProvider("A", new TorrentSearchResult { Title = "Movie (A)", InfoHash = hash, Seeders = 10 });
        var b = new FakeProvider("B", new TorrentSearchResult { Title = "Movie (B)", InfoHash = hash, Seeders = 42 });
        var service = CreateService(a, b);

        var results = await service.SearchAsync("movie");

        results.Should().ContainSingle();
        results[0].Seeders.Should().Be(42);
        results[0].Title.Should().Be("Movie (B)");
    }

    [Fact]
    public async Task SearchAsync_KeepsDistinctBlankHashResults_InsteadOfCollapsingThem()
    {
        // Two unrelated torrents from a provider that resolves magnets lazily — both have a blank
        // hash at search time. The regression this guards: grouping by hash merges them into one.
        var lazy = new FakeProvider("Lazy",
            new TorrentSearchResult { Title = "Show S01E01", InfoHash = "", Seeders = 5 },
            new TorrentSearchResult { Title = "Show S01E02", InfoHash = "", Seeders = 3 });
        var service = CreateService(lazy);

        var results = await service.SearchAsync("show");

        results.Should().HaveCount(2);
        results.Select(r => r.Title).Should().BeEquivalentTo("Show S01E01", "Show S01E02");
    }

    [Fact]
    public async Task SearchAsync_DedupsRealHashes_WhilePreservingBlankHashResults()
    {
        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        var provider = new FakeProvider("Mixed",
            new TorrentSearchResult { Title = "Dup A", InfoHash = hash, Seeders = 8 },
            new TorrentSearchResult { Title = "Dup B", InfoHash = hash, Seeders = 20 },
            new TorrentSearchResult { Title = "Unresolved", InfoHash = "", Seeders = 100 });
        var service = CreateService(provider);

        var results = await service.SearchAsync("x");

        // One row for the deduped real hash, plus the untouched blank-hash row; sorted by seeders.
        results.Should().HaveCount(2);
        results.Select(r => r.Title).Should().BeEquivalentTo("Unresolved", "Dup B");
    }

    private sealed class FakeProvider : ITorrentSearchProvider
    {
        private readonly IReadOnlyList<TorrentSearchResult> _results;
        public FakeProvider(string name, params TorrentSearchResult[] results)
        {
            Name = name;
            foreach (var r in results) r.Source = name;
            _results = results;
        }

        public string Name { get; }
        public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(_results);
    }
}
