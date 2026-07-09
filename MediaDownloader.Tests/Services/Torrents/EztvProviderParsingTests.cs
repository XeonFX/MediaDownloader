using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

public class EztvProviderParsingTests
{
    private static readonly string Json = FixtureLoader.Load("eztv-api.json");

    [Fact]
    public void ParsePage_SkipsEntryWithEmptyHash()
    {
        var page = EztvProvider.ParsePage(Json);

        page.Torrents.Should().HaveCount(2);
        page.Torrents.Should().NotContain(r => r.Title.Contains("Charm City"));
    }

    [Fact]
    public void ParsePage_ExtractsFirstRowFieldsCorrectly()
    {
        var page = EztvProvider.ParsePage(Json);

        var result = page.Torrents[0];
        result.Title.Should().Be("Law and Order S06E11 Corpus Delicti 720p HEVC x265-MeGusta EZTV");
        result.InfoHash.Should().Be("2fa9d6729a9cf935e4e53cc3c8cd16d619561c06");
        result.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:2fa9d6729a9cf935e4e53cc3c8cd16d619561c06");
        result.SizeBytes.Should().Be(281349973);
        result.Seeders.Should().Be(41);
        result.Leechers.Should().Be(3);
        result.PublishedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1783605253).UtcDateTime);
        result.Source.Should().Be("EZTV");
        result.IsRealInfoHash.Should().BeTrue();
    }

    [Fact]
    public void ParsePage_AcceptsUppercaseHash()
    {
        var page = EztvProvider.ParsePage(Json);

        var result = page.Torrents[1];
        result.InfoHash.Should().Be("FE8F7271B12545E07DBFF0A265D2BC40DC5861EF");
        result.IsRealInfoHash.Should().BeTrue();
    }

    [Fact]
    public void ParsePage_ReadsTotalCount()
    {
        var page = EztvProvider.ParsePage(Json);

        page.TotalCount.Should().Be(219);
    }

    [Fact]
    public void ParsePage_MissingTorrentsProperty_ReturnsEmptyList()
    {
        var page = EztvProvider.ParsePage("{\"torrents_count\": 0}");

        page.Torrents.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }
}
