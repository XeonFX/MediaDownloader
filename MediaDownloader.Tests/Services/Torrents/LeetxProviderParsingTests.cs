using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

public class LeetxProviderParsingTests
{
    private static readonly string Html = FixtureLoader.Load("leetx-search.html");

    [Fact]
    public void ParseRows_ExtractsAllRows()
    {
        var rows = LeetxProvider.ParseRows(Html).ToList();

        rows.Should().HaveCount(2);
    }

    [Fact]
    public void ParseRows_ExtractsFirstRowFieldsCorrectly()
    {
        var row = LeetxProvider.ParseRows(Html).First();

        row.Title.Should().Be("Ubuntu MATE 16.04.2 [MATE][armhf][img.xz][Uzerus]");
        row.DetailPath.Should().Be("/torrent/2099267/Ubuntu-MATE-16-04-2-MATE-armhf-img-xz-Uzerus/");
        row.SizeBytes.Should().Be(1181116006); // 1.1 GB
        row.Seeders.Should().Be(260);
        row.Leechers.Should().Be(2);
        row.Published.Should().Be(new DateTime(2017, 6, 20));
    }

    [Fact]
    public void ParseRows_PicksTitleAnchor_NotTheIconOnlyAnchor()
    {
        // The name cell has two anchors: an icon-only category link with no text, and the real
        // title link. Regression check that we don't accidentally pick up the category link.
        var row = LeetxProvider.ParseRows(Html).First();

        row.DetailPath.Should().StartWith("/torrent/");
        row.DetailPath.Should().NotBe("/sub/apps/Linux/1/");
    }

    [Fact]
    public void ToResult_BuildsPlaceholderHashFromTorrentId()
    {
        var row = LeetxProvider.ParseRows(Html).First();

        var result = LeetxProvider.ToResult("www.1377x.to", row);

        result.InfoHash.Should().Be("1337x-2099267");
        result.Source.Should().Be("1337x");
        result.DetailsUrl.Should().Be("https://www.1377x.to/torrent/2099267/Ubuntu-MATE-16-04-2-MATE-armhf-img-xz-Uzerus/");
        result.NeedsResolution.Should().BeTrue(); // no magnet until the detail page is resolved
    }
}
