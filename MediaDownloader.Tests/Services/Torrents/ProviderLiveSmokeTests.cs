using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MediaDownloader.Tests.Services.Torrents;

/// <summary>
/// Hits each provider's real site with a query that must have results, and fails if it comes back
/// empty or unparseable.
///
/// Fixture tests prove the parsers still handle the markup as it was when captured; only this can
/// tell you the markup changed, or that a site started refusing us — which is how RARBG sat behind a
/// Cloudflare challenge, returning nothing on every search, without the suite noticing.
///
/// Skipped by default (see <see cref="LiveFactAttribute"/>). Run with:
/// <code>MD_LIVE_TESTS=1 dotnet test --filter Category=Live</code>
/// </summary>
[Trait("Category", "Live")]
public class ProviderLiveSmokeTests(ITestOutputHelper output)
{
    // Queries chosen to be permanently populated on the site in question, and safe for work.
    private const string GeneralQuery = "ubuntu";
    private const string AnimeQuery = "subsplease";

    private static readonly ServiceProvider Services = BuildServices();

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("torrent-search", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MediaDownloader/1.0");
        });
        return services.BuildServiceProvider();
    }

    private static IHttpClientFactory Http => Services.GetRequiredService<IHttpClientFactory>();

    /// <summary>Caps a live call so a hanging site fails the test instead of stalling the run.</summary>
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token;

    private async Task AssertLiveSearchWorksAsync(ITorrentSearchProvider provider, string query)
    {
        var results = await provider.SearchAsync(query, Timeout());

        output.WriteLine($"{provider.Name}: {results.Count} results for '{query}'");
        foreach (var r in results.OrderByDescending(r => r.Seeders).Take(3))
            output.WriteLine($"  {r.Seeders,6} seeds  {r.SizeDisplay,10}  {r.Title}");

        results.Should().NotBeEmpty($"{provider.Name} should return results for '{query}'");
        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Title));
        results.Should().NotContain(r => r.Title.All(char.IsDigit),
            "a numeric title means a selector is picking up the wrong element");
        results.Should().Contain(r => r.SizeBytes > 0, "sizes should parse");
        results.Should().Contain(r => r.Seeders > 0, "seeder counts should parse");
    }

    [LiveFact]
    public Task Nyaa() => AssertLiveSearchWorksAsync(new NyaaProvider(Http), AnimeQuery);

    [LiveFact]
    public Task PirateBay() => AssertLiveSearchWorksAsync(new PirateBayProvider(Http), GeneralQuery);

    [LiveFact]
    public Task Rarbg() => AssertLiveSearchWorksAsync(new RarbgProvider(Http), GeneralQuery);

    [LiveFact]
    public Task Leetx() => AssertLiveSearchWorksAsync(new LeetxProvider(Http), GeneralQuery);

    [LiveFact]
    public Task Eztv() => AssertLiveSearchWorksAsync(new EztvProvider(Http), "1080p");

    [LiveFact]
    public Task TorrentsCsv() => AssertLiveSearchWorksAsync(new TorrentsCsvProvider(Http), GeneralQuery);

    [LiveFact]
    public async Task RarbgResolvesADescription()
    {
        var provider = new RarbgProvider(Http);
        var results = await provider.SearchAsync(GeneralQuery, Timeout());

        var details = await provider.GetDetailsAsync(results[0], Timeout());

        output.WriteLine($"{results[0].Title}: {details.Description?[..Math.Min(120, details.Description.Length)]}");
        details.Description.Should().NotBeNullOrWhiteSpace();
    }
}
