using FluentAssertions;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

public class PirateBayProviderParsingTests
{
    [Fact]
    public void ParseApi_ExtractsValidEntries()
    {
        var json = FixtureLoader.Load("piratebay-api.json");

        var results = PirateBayProvider.ParseApi(json);

        // The middle entry has an empty info_hash and the "0" id is apibay's placeholder for "no
        // results" — both must be skipped, leaving exactly one usable result.
        results.Should().HaveCount(1);
        var result = results[0];
        result.Title.Should().Be("ubuntu-26.04-desktop-amd64.iso");
        result.InfoHash.Should().Be("DAFC8C076CA2F3ED376EEAE7C76A0D6BE2415C45");
        result.SizeBytes.Should().Be(6517612871);
        result.Seeders.Should().Be(137);
        result.Leechers.Should().Be(18);
        result.Source.Should().Be("The Pirate Bay");
    }

    [Fact]
    public void ParseApi_SkipsEntryWithEmptyInfoHash()
    {
        var json = FixtureLoader.Load("piratebay-api.json");

        var results = PirateBayProvider.ParseApi(json);

        results.Should().NotContain(r => r.Title == "ubuntu-24.04.1-desktop-amd64.iso");
    }

    [Fact]
    public void ParseMirrorHtml_DoubleLayout_ExtractsBothRowsCorrectly()
    {
        // tpb.party's layout: title anchor, a plain date cell, a magnet anchor, then three
        // right-aligned cells (size, seeders, leechers).
        var html = FixtureLoader.Load("piratebay-mirror-double.html");

        var results = PirateBayProvider.ParseMirrorHtml(html);

        results.Should().HaveCount(2);
        var first = results[0];
        first.Title.Should().Be("ubuntu-26.04-desktop-amd64.iso");
        first.InfoHash.Should().Be("DAFC8C076CA2F3ED376EEAE7C76A0D6BE2415C45");
        first.SizeBytes.Should().Be(6517612871); // 6.07 GiB
        first.Seeders.Should().Be(137);
        first.Leechers.Should().Be(18);
        first.PublishedAt.Should().Be(new DateTime(DateTime.UtcNow.Year, 4, 25, 16, 35, 0, DateTimeKind.Utc));

        var second = results[1];
        second.SizeBytes.Should().Be(6216965160); // 5.79 GiB
        second.Seeders.Should().Be(42);
        second.Leechers.Should().Be(12);
        second.PublishedAt.Should().Be(new DateTime(2024, 9, 8));
    }

    [Fact]
    public void ParseMirrorHtml_SingleLayout_ExtractsBothRowsCorrectly()
    {
        // piratebay.live's layout: title + magnet anchors plus a single <font class="detDesc">
        // blob packing "Uploaded X, Size Y, ULed by Z", then only two right-aligned cells
        // (seeders, leechers) — this is the layout the old regex-based parser got wrong (always
        // read size as 0 and misaligned seeders/leechers with the non-existent third cell).
        var html = FixtureLoader.Load("piratebay-mirror-single.html");

        var results = PirateBayProvider.ParseMirrorHtml(html);

        results.Should().HaveCount(2);
        var first = results[0];
        first.Title.Should().Be("ubuntu-26.04-desktop-amd64.iso");
        first.InfoHash.Should().Be("DAFC8C076CA2F3ED376EEAE7C76A0D6BE2415C45");
        first.SizeBytes.Should().Be(6517612871); // 6.07 GiB
        first.Seeders.Should().Be(137);
        first.Leechers.Should().Be(18);
        first.PublishedAt.Should().Be(new DateTime(DateTime.UtcNow.Year, 4, 25, 16, 35, 0, DateTimeKind.Utc));

        var second = results[1];
        second.SizeBytes.Should().Be(6216965160); // 5.79 GiB
        second.Seeders.Should().Be(42);
        second.Leechers.Should().Be(12);
        second.PublishedAt.Should().Be(new DateTime(2024, 9, 8));
    }

    [Fact]
    public void ParseMirrorHtml_BothLayouts_ProduceIdenticalResultsForTheSameTorrent()
    {
        // The two mirrors serve the same underlying data in different table shapes; the parser
        // must normalize both to the same result.
        var doubleResult = PirateBayProvider.ParseMirrorHtml(FixtureLoader.Load("piratebay-mirror-double.html"))[0];
        var singleResult = PirateBayProvider.ParseMirrorHtml(FixtureLoader.Load("piratebay-mirror-single.html"))[0];

        doubleResult.InfoHash.Should().Be(singleResult.InfoHash);
        doubleResult.SizeBytes.Should().Be(singleResult.SizeBytes);
        doubleResult.Seeders.Should().Be(singleResult.Seeders);
        doubleResult.Leechers.Should().Be(singleResult.Leechers);
        doubleResult.PublishedAt.Should().Be(singleResult.PublishedAt);
    }
}
