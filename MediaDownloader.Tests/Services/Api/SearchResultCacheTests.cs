using FluentAssertions;
using MediaDownloader.Services.Api;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Tests.Services.Api;

public class SearchResultCacheTests
{
    [Fact]
    public void AddAndGet_ReturnsSameLiveResult()
    {
        var cache = new SearchResultCache();
        var result = Result("first");

        var id = cache.Add(result);

        id.Should().StartWith("r_");
        cache.Get(id).Should().BeSameAs(result);
    }

    [Fact]
    public void Get_UsesSlidingExpiration()
    {
        var clock = new ManualTimeProvider();
        var cache = new SearchResultCache(clock);
        var id = cache.Add(Result("first"));

        clock.Advance(TimeSpan.FromMinutes(29));
        cache.Get(id).Title.Should().Be("first");
        clock.Advance(TimeSpan.FromMinutes(29));
        cache.Get(id).Title.Should().Be("first");
        clock.Advance(TimeSpan.FromMinutes(31));

        var act = () => cache.Get(id);
        act.Should().Throw<AgentApiException>()
            .Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void Add_EvictsOldestEntry_WhenBoundIsExceeded()
    {
        var cache = new SearchResultCache();
        var oldest = cache.Add(Result("oldest"));

        for (var i = 0; i < 2_000; i++)
            cache.Add(Result($"row-{i}"));

        cache.Count.Should().Be(2_000);
        var act = () => cache.Get(oldest);
        act.Should().Throw<AgentApiException>();
    }

    private static TorrentSearchResult Result(string title) => new()
    {
        Title = title,
        Source = "Test",
        InfoHash = new string('a', 40),
        MagnetUri = "magnet:?xt=urn:btih:" + new string('a', 40)
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 3, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
