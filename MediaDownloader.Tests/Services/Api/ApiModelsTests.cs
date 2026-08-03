using FluentAssertions;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Api;

namespace MediaDownloader.Tests.Services.Api;

public class ApiModelsTests
{
    [Fact]
    public void DownloadDto_MapsPersistedAndLiveFields_WithoutEntityNavigation()
    {
        var item = new DownloadItem
        {
            Id = 42,
            Name = "Ubuntu",
            Status = DownloadStatus.Downloading,
            Progress = 37.5,
            TotalBytes = 1_000,
            DownloadSpeed = 250,
            UploadSpeed = 10,
            Peers = 7,
            Source = "Agent",
            SavePath = "/downloads",
            SeriesTaskId = 3,
            Error = null
        };

        var dto = DownloadDto.From(item);

        dto.Should().BeEquivalentTo(new
        {
            item.Id,
            item.Name,
            Status = "Downloading",
            item.Progress,
            item.TotalBytes,
            item.DownloadSpeed,
            item.UploadSpeed,
            item.Peers,
            item.Source,
            item.SavePath,
            item.SeriesTaskId,
            item.Error
        });
    }
}
