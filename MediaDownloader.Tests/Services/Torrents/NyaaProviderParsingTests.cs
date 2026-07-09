using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

public class NyaaProviderParsingTests
{
    private static readonly string Html = FixtureLoader.Load("nyaa-search.html");

    [Fact]
    public void ParseRows_ExtractsResultCorrectly()
    {
        var results = NyaaProvider.ParseRows(Html);

        results.Should().HaveCount(1);
        var result = results[0];

        result.Title.Should().Be("Koha Live CD Release 3 (3.0.4 Ubuntu 9.10 Desktop x86)");
        result.InfoHash.Should().Be("45008e48c8800b7d7643337b2e70a634e4c69f6a");
        result.SizeBytes.Should().Be(654311424); // 624.0 MiB
        result.Seeders.Should().Be(42);
        result.Leechers.Should().Be(3);
        result.Source.Should().Be("Nyaa");
        result.DetailsUrl.Should().Be("https://nyaa.si/view/96659");
    }

    [Fact]
    public void ParseRows_UsesTimestampAttribute_ForPublishedDate()
    {
        var result = NyaaProvider.ParseRows(Html)[0];

        result.PublishedAt.Should().Be(new DateTime(2009, 11, 3, 7, 3, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseRows_BuildsMagnetWithNyaaTrackers()
    {
        var result = NyaaProvider.ParseRows(Html)[0];

        result.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:45008e48c8800b7d7643337b2e70a634e4c69f6a");
        result.MagnetUri.Should().Contain(Uri.EscapeDataString("http://nyaa.tracker.wf:7777/announce"));
    }
}
