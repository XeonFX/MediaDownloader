using FluentAssertions;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Downloads;
using MonoTorrent.Client;

namespace MediaDownloader.Tests.Services.Downloads;

/// <summary>
/// Covers DownloadManager.MapState, the switch that turns a MonoTorrent engine state into the
/// app's own DownloadStatus. Previously untested despite being the piece that decides, among other
/// things, whether a stopped torrent shows up as Completed or Paused after a restart.
/// </summary>
public class DownloadManagerMapStateTests
{
    private static DownloadItem Item(double progress = 0, DownloadStatus status = DownloadStatus.Queued) =>
        new() { Progress = progress, Status = status };

    [Theory]
    [InlineData(TorrentState.Downloading, DownloadStatus.Downloading)]
    [InlineData(TorrentState.Seeding, DownloadStatus.Seeding)]
    [InlineData(TorrentState.Paused, DownloadStatus.Paused)]
    [InlineData(TorrentState.Hashing, DownloadStatus.Downloading)]
    [InlineData(TorrentState.HashingPaused, DownloadStatus.Downloading)]
    [InlineData(TorrentState.Metadata, DownloadStatus.FetchingMetadata)]
    [InlineData(TorrentState.Error, DownloadStatus.Error)]
    public void MapsDirectStates(TorrentState torrentState, DownloadStatus expected)
    {
        DownloadManager.MapState(torrentState, Item()).Should().Be(expected);
    }

    [Theory]
    [InlineData(TorrentState.Stopped)]
    [InlineData(TorrentState.Stopping)]
    public void StoppedOrStopping_MapsToCompleted_WhenProgressIsFull(TorrentState torrentState)
    {
        DownloadManager.MapState(torrentState, Item(progress: 100)).Should().Be(DownloadStatus.Completed);
    }

    [Theory]
    [InlineData(TorrentState.Stopped)]
    [InlineData(TorrentState.Stopping)]
    public void StoppedOrStopping_MapsToPaused_WhenProgressIsIncomplete(TorrentState torrentState)
    {
        DownloadManager.MapState(torrentState, Item(progress: 42.5)).Should().Be(DownloadStatus.Paused);
    }

    [Theory]
    [InlineData(TorrentState.Starting)]
    [InlineData(TorrentState.FetchingHashes)]
    public void UnmappedStates_LeaveExistingStatusUnchanged(TorrentState torrentState)
    {
        var item = Item(status: DownloadStatus.Seeding);

        DownloadManager.MapState(torrentState, item).Should().Be(DownloadStatus.Seeding);
    }
}
