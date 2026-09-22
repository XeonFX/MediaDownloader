using FluentAssertions;
using MediaDownloader.Services.Downloads;

namespace MediaDownloader.Tests.Services.Downloads;

public class DownloadDirectoryCleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("md-cleanup-").FullName;

    [Fact]
    public void PreservesUnrelatedFilesAndRemovesOnlyEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty", "nested"));
        var occupied = Directory.CreateDirectory(Path.Combine(_root, "occupied")).FullName;
        var file = Path.Combine(occupied, "user-document.txt");
        File.WriteAllText(file, "keep me");

        DownloadDirectoryCleanup.RemoveEmptyTree(_root);

        File.ReadAllText(file).Should().Be("keep me");
        Directory.Exists(Path.Combine(_root, "empty")).Should().BeFalse();
    }

    [Fact]
    public void RemovesAnEmptyTorrentDirectory()
    {
        DownloadDirectoryCleanup.RemoveEmptyTree(_root);
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public void DoesNotTraverseDirectoryLinks()
    {
        var outside = Directory.CreateTempSubdirectory("md-cleanup-outside-").FullName;
        var empty = Directory.CreateDirectory(Path.Combine(outside, "empty")).FullName;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            DownloadDirectoryCleanup.RemoveEmptyTree(_root);
            Directory.Exists(empty).Should().BeTrue();
            new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
