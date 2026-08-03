using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

public class RarbgProviderParsingTests
{
    private static readonly string Json = FixtureLoader.Load("rarbg-search.json");

    [Fact]
    public void ParsePage_ExtractsEveryRowAndTheTotal()
    {
        var page = RarbgProvider.ParsePage(Json);

        page.Results.Should().HaveCount(43);
        page.Total.Should().Be(43);
    }

    [Fact]
    public void ParsePage_ExtractsFirstRowFieldsCorrectly()
    {
        var result = RarbgProvider.ParsePage(Json).Results[0];

        result.Title.Should().Be("ubuntucinnamon-26.04-desktop-amd64.iso");
        result.InfoHash.Should().Be("8586FE65D6B589ACA262DBBC164570C335BD7D37");
        result.SizeBytes.Should().Be(5659195392);
        result.Seeders.Should().Be(40);
        result.Leechers.Should().Be(11);
        result.Source.Should().Be("RARBG");
        result.DetailsUrl.Should().Be("https://therarbg.com/post-detail/8a715c/x/");
    }

    [Fact]
    public void ParsePage_ProducesRealInfoHashesAndUsableMagnets()
    {
        // The whole point of moving off the old HTML scraper: rows carry a real info hash, so a
        // download can start straight from the search list with no detail-page round trip.
        var results = RarbgProvider.ParsePage(Json).Results;

        results.Should().OnlyContain(r => r.IsRealInfoHash);
        results.Should().OnlyContain(r => !r.NeedsResolution);
        results[0].MagnetUri.Should().StartWith("magnet:?xt=urn:btih:8586FE65D6B589ACA262DBBC164570C335BD7D37");
    }

    [Fact]
    public void ParsePage_ReadsAddedTimestampAsUtc()
    {
        var result = RarbgProvider.ParsePage(Json).Results[0];

        result.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1777054855).UtcDateTime);
        result.PublishedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void ParsePage_SkipsRowsWithoutAHash()
    {
        const string json = """
            {"total":2,"results":[
              {"pk":"a1","n":"Has hash","h":"8586FE65D6B589ACA262DBBC164570C335BD7D37","s":10,"se":1,"le":0,"a":1700000000},
              {"pk":"a2","n":"No hash","h":null,"s":10,"se":9,"le":0,"a":1700000000}
            ]}
            """;

        RarbgProvider.ParsePage(json).Results.Should().ContainSingle().Which.Title.Should().Be("Has hash");
    }

    [Fact]
    public void ParsePage_ToleratesAMissingResultsArray()
    {
        RarbgProvider.ParsePage("""{"detail":"Not found"}""").Results.Should().BeEmpty();
    }

    [Fact]
    public void ParseDetail_ExtractsTheDescription()
    {
        var details = RarbgProvider.ParseDetail(FixtureLoader.Load("rarbg-detail.json"));

        details.Description.Should().StartWith("Ubuntu 26.04 LTS");
    }

    [Fact]
    public void ParseDetail_TreatsABlankDescriptionAsAbsent()
    {
        RarbgProvider.ParseDetail("""{"descr":"   "}""").Description.Should().BeNull();
    }
}
