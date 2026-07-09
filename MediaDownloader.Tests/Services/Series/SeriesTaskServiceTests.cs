using FluentAssertions;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Series;
using MediaDownloader.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaDownloader.Tests.Services.Series;

public class SeriesTaskServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SeriesTaskService _service;

    public SeriesTaskServiceTests()
    {
        var (factory, connection) = TestDb.CreateFactory();
        _connection = connection;
        _service = new SeriesTaskService(factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetAllAsync_ReturnsEmptyList_WhenNoTasksExist()
    {
        var tasks = await _service.GetAllAsync();

        tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_PersistsTask_AndAssignsId()
    {
        var task = new SeriesTask { Name = "One Piece", Query = "one piece 1080p" };

        var added = await _service.AddAsync(task);

        added.Id.Should().BeGreaterThan(0);
        (await _service.GetAllAsync()).Should().ContainSingle(t => t.Name == "One Piece");
    }

    [Fact]
    public async Task SaveAsync_PersistsChangesToExistingTask()
    {
        var task = await _service.AddAsync(new SeriesTask { Name = "Original" });

        task.Name = "Renamed";
        await _service.SaveAsync(task);

        var reloaded = await _service.GetAllAsync();
        reloaded.Should().ContainSingle(t => t.Name == "Renamed");
    }

    [Fact]
    public async Task DeleteAsync_RemovesTask()
    {
        var task = await _service.AddAsync(new SeriesTask { Name = "To delete" });

        await _service.DeleteAsync(task);

        (await _service.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetEffectiveDownloadFolderAsync_ReturnsTaskFolder_WhenSet()
    {
        var task = new SeriesTask { DownloadFolder = "/custom/folder" };

        var folder = await _service.GetEffectiveDownloadFolderAsync(task);

        folder.Should().Be("/custom/folder");
    }

    [Fact]
    public async Task GetEffectiveDownloadFolderAsync_FallsBackToGlobalSettings_WhenTaskFolderUnset()
    {
        var task = new SeriesTask { DownloadFolder = null };

        var folder = await _service.GetEffectiveDownloadFolderAsync(task);

        // Falls back to AppSettings.DownloadFolder's default (a real, non-empty path).
        folder.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsTasksOrderedById()
    {
        var first = await _service.AddAsync(new SeriesTask { Name = "First" });
        var second = await _service.AddAsync(new SeriesTask { Name = "Second" });
        var third = await _service.AddAsync(new SeriesTask { Name = "Third" });

        var tasks = await _service.GetAllAsync();

        tasks.Select(t => t.Id).Should().Equal(first.Id, second.Id, third.Id);
    }
}
