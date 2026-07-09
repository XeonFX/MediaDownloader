using FluentAssertions;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Tests.Services.Torrents;

public class MagnetTests
{
    private const string InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public void Build_IncludesInfoHashAndDisplayName()
    {
        var magnet = Magnet.Build(InfoHash, "My Torrent");

        magnet.Should().StartWith($"magnet:?xt=urn:btih:{InfoHash}");
        magnet.Should().Contain("dn=My%20Torrent");
    }

    [Fact]
    public void Build_AppendsAllDefaultTrackers()
    {
        var magnet = Magnet.Build(InfoHash, "Name");

        foreach (var tracker in Magnet.DefaultTrackers)
        {
            magnet.Should().Contain("tr=" + Uri.EscapeDataString(tracker));
        }
    }

    [Fact]
    public void Build_UsesProvidedTrackers_InsteadOfDefaults()
    {
        var customTrackers = new[] { "udp://custom.tracker:80/announce" };

        var magnet = Magnet.Build(InfoHash, "Name", customTrackers);

        magnet.Should().Contain(Uri.EscapeDataString(customTrackers[0]));
        magnet.Should().NotContain(Uri.EscapeDataString(Magnet.DefaultTrackers[0]));
    }

    [Fact]
    public void ExtractInfoHash_ReturnsHash_WhenPresent()
    {
        var magnet = $"magnet:?xt=urn:btih:{InfoHash}&dn=Name";

        Magnet.ExtractInfoHash(magnet).Should().Be(InfoHash);
    }

    [Fact]
    public void ExtractInfoHash_ReturnsNull_WhenAbsent()
    {
        Magnet.ExtractInfoHash("magnet:?dn=NoHashHere").Should().BeNull();
    }

    [Fact]
    public void ExtractInfoHash_IsCaseInsensitive()
    {
        var magnet = "magnet:?XT=URN:BTIH:abc123&dn=Name";

        Magnet.ExtractInfoHash(magnet).Should().Be("abc123");
    }
}
