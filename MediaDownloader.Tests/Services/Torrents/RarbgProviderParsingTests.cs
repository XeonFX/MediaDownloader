using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

public class RarbgProviderParsingTests
{
    private static readonly string Html = FixtureLoader.Load("rarbg-search.html");

    [Fact]
    public void ParseRows_ExtractsAllRows()
    {
        var rows = RarbgProvider.ParseRows(Html).ToList();

        rows.Should().HaveCount(2);
    }

    [Fact]
    public void ParseRows_ExtractsFirstRowFieldsCorrectly()
    {
        var row = RarbgProvider.ParseRows(Html).First();

        row.Title.Should().Be("Ubuntu MATE 16.04.2 [MATE][armhf][img.xz][Uzerus]");
        row.DetailPath.Should().Be("/torrent/ubuntu-mate-16-04-2-mate-armhf-img-xz-uzerus-2099267.html");
        row.SizeBytes.Should().Be(1181116006); // 1.1 GB
        row.Seeders.Should().Be(260); // wrapped in <font>
        row.Leechers.Should().Be(2); // plain cell, same width
        row.Published.Should().Be(new DateTime(2017, 6, 20, 16, 8, 36));
    }

    [Fact]
    public void ParseRows_DoesNotConfuseCategoryDateAndSeedLeechCells()
    {
        // Both the category cell and the date cell are width:150px, and only the date cell's text
        // matches the yyyy-MM-dd HH:mm:ss shape — regression check that date parsing didn't
        // accidentally grab the category cell instead.
        var row = RarbgProvider.ParseRows(Html).Skip(1).First();

        row.Published.Should().Be(new DateTime(2018, 8, 13, 4, 5, 4));
        row.SizeBytes.Should().Be(80740352); // 77 MB
    }

    [Fact]
    public void ToResult_BuildsPlaceholderHashFromTorrentId()
    {
        var row = RarbgProvider.ParseRows(Html).First();

        var result = RarbgProvider.ToResult("www2.rarbggo.to", row);

        result.InfoHash.Should().Be("rarbg-2099267");
        result.Source.Should().Be("RARBG");
        result.DetailsUrl.Should().Be("https://www2.rarbggo.to/torrent/ubuntu-mate-16-04-2-mate-armhf-img-xz-uzerus-2099267.html");
    }
}
