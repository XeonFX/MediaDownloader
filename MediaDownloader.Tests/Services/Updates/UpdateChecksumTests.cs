using FluentAssertions;
using MediaDownloader.Services.Updates;

namespace MediaDownloader.Tests.Services.Updates;

/// <summary>
/// Covers parsing of the SHA256SUMS.txt the release workflow publishes, which the updater checks a
/// downloaded zip against before letting it replace the running app.
/// </summary>
public class UpdateChecksumTests
{
    private const string Manifest = """
        3b2d1f0a9c8e7d6b5a4938271605f4e3d2c1b0a998877665544332211ffeeddc  MediaDownloader-1.1.0-osx-arm64.zip
        aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899  MediaDownloader-1.1.0-osx-x64.zip
        99887766554433221100ffeeddccbbaa99887766554433221100ffeeddccbbaa  MediaDownloader-1.1.0-win-x64.zip
        """;

    [Fact]
    public void FindChecksum_ReturnsTheHashForTheRequestedAsset()
    {
        UpdateService.FindChecksum(Manifest, "MediaDownloader-1.1.0-osx-x64.zip")
            .Should().Be("aa11bb22cc33dd44ee55ff6677889900aabbccddeeff00112233445566778899");
    }

    [Fact]
    public void FindChecksum_HandlesBinaryModeEntries()
    {
        // sha256sum writes "*" before the filename when the file was read in binary mode.
        const string binaryMode = "aa11bb22cc33dd44 *MediaDownloader-1.1.0-osx-arm64.zip";

        UpdateService.FindChecksum(binaryMode, "MediaDownloader-1.1.0-osx-arm64.zip")
            .Should().Be("aa11bb22cc33dd44");
    }

    [Fact]
    public void FindChecksum_ToleratesWindowsLineEndingsAndBlankLines()
    {
        var crlf = Manifest.Replace("\n", "\r\n") + "\r\n\r\n";

        UpdateService.FindChecksum(crlf, "MediaDownloader-1.1.0-win-x64.zip")
            .Should().Be("99887766554433221100ffeeddccbbaa99887766554433221100ffeeddccbbaa");
    }

    [Fact]
    public void FindChecksum_ReturnsNullForAnAssetTheManifestDoesNotList()
    {
        // The installer treats this as a hard failure rather than installing unverified.
        UpdateService.FindChecksum(Manifest, "MediaDownloader-1.1.0-linux-x64.zip").Should().BeNull();
    }

    [Fact]
    public void FindChecksum_DoesNotMatchOnAPartialFilename()
    {
        UpdateService.FindChecksum(Manifest, "osx-x64.zip").Should().BeNull();
    }
}
