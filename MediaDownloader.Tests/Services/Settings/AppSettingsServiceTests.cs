using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Settings;
using MediaDownloader.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaDownloader.Tests.Services.Settings;

public class AppSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppSettingsService _service;

    public AppSettingsServiceTests()
    {
        var (factory, connection) = TestDb.CreateFactory();
        _connection = connection;
        _service = new AppSettingsService(factory);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetAsync_CreatesDefaultRow_OnFirstCall()
    {
        var settings = await _service.GetAsync();

        settings.Id.Should().Be(1);
        settings.DownloadFolder.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetAsync_ReturnsSameRow_OnSubsequentCalls()
    {
        var first = await _service.GetAsync();
        first.Language = "pl";
        await _service.SaveAsync(first);

        var second = await _service.GetAsync();

        second.Language.Should().Be("pl");
    }

    [Fact]
    public async Task SaveAsync_PersistsValidChanges()
    {
        var settings = await _service.GetAsync();
        settings.SmtpHost = "smtp.example.com";
        settings.SmtpPort = 465;

        await _service.SaveAsync(settings);

        var reloaded = await _service.GetAsync();
        reloaded.SmtpHost.Should().Be("smtp.example.com");
        reloaded.SmtpPort.Should().Be(465);
    }

    [Fact]
    public async Task SaveAsync_Throws_ForInvalidSmtpPort()
    {
        var settings = await _service.GetAsync();
        settings.SmtpPort = 99999;

        var act = () => _service.SaveAsync(settings);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task SaveAsync_Throws_ForMalformedEmailAddress()
    {
        var settings = await _service.GetAsync();
        settings.EmailFrom = "not-an-email";

        var act = () => _service.SaveAsync(settings);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task GetCredentialsAsync_ReturnsEmptyList_WhenNoneSaved()
    {
        var credentials = await _service.GetCredentialsAsync();

        credentials.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveCredentialAsync_AddsNewCredential_WhenIdIsZero()
    {
        var credential = new ProviderCredential { ProviderName = "PTE", Username = "alice", Password = "hunter2" };

        await _service.SaveCredentialAsync(credential);

        var credentials = await _service.GetCredentialsAsync();
        credentials.Should().ContainSingle(c => c.ProviderName == "PTE" && c.Username == "alice");
    }

    [Fact]
    public async Task SaveCredentialAsync_UpdatesExistingCredential_WhenIdIsSet()
    {
        var credential = new ProviderCredential { ProviderName = "PTE", Username = "alice", Password = "hunter2" };
        await _service.SaveCredentialAsync(credential);

        credential.Password = "new-password";
        await _service.SaveCredentialAsync(credential);

        var credentials = await _service.GetCredentialsAsync();
        credentials.Should().ContainSingle().Which.Password.Should().Be("new-password");
    }
}
