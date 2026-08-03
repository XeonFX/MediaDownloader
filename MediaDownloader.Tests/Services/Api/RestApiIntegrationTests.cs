using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MediaDownloader.Data;
using MediaDownloader.Services.Api;
using MediaDownloader.Services.Downloads;
using MediaDownloader.Services.Torrents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;

namespace MediaDownloader.Tests.Services.Api;

public sealed class RestApiIntegrationTests : IAsyncLifetime
{
    private readonly TestApplication _app = new();
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        // The real default is off and should look like a missing route.
        (await _client.GetAsync("/api/settings")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = _app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var settings = await db.GetSettingsAsync();
        settings.AgentApiEnabled = true;
        settings.AgentApiAllowRemote = true;
        settings.DownloadFolder = _app.DownloadDirectory;
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<AgentAccess>().Invalidate();

        // TestServer has no real socket address, so it correctly receives no loopback exemption.
        // Exercise the remote branch with the generated bearer token.
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AgentApiToken);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Search_Start_List_AndDelete_FollowsOneResultHandle()
    {
        var searchResponse = await _client.PostAsJsonAsync("/api/search", new SearchRequest("ubuntu"));
        searchResponse.EnsureSuccessStatusCode();
        var search = await searchResponse.Content.ReadFromJsonAsync<SearchResponse>();
        search.Should().NotBeNull();
        search!.Sources.Should().ContainSingle(s => s.Source == "Integration" && s.Status == "ok");
        var result = search.Results.Should().ContainSingle().Subject;
        result.ResultId.Should().StartWith("r_");

        var startResponse = await _client.PostAsJsonAsync("/api/downloads", new StartDownloadRequest(ResultId: result.ResultId));
        startResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var started = await startResponse.Content.ReadFromJsonAsync<DownloadDto>();
        started.Should().NotBeNull();
        started!.Name.Should().Be("Ubuntu Integration Image");

        var downloads = await _client.GetFromJsonAsync<List<DownloadDto>>("/api/downloads");
        downloads.Should().ContainSingle(d => d.Id == started.Id);

        var deleteResponse = await _client.DeleteAsync($"/api/downloads/{started.Id}");
        deleteResponse.EnsureSuccessStatusCode();
        (await _client.GetFromJsonAsync<List<DownloadDto>>("/api/downloads")).Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_Handshake_AdvertisesTheFacadeToolSet()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_client.BaseAddress!, "/mcp"),
            Name = "integration-test"
        }, _client, loggerFactory: null, ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport);

        var tools = await mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(new[]
        {
            "list_sources", "search_torrents", "get_torrent_details", "get_settings",
            "start_download", "list_downloads", "pause_download", "resume_download", "delete_download",
            "list_series_tasks", "create_series_task", "update_series_task", "delete_series_task",
            "check_series_task_now"
        });

        var result = await mcp.CallToolAsync("list_sources");
        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenApi_DescribesResponseBodies()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));
        var schema = document.RootElement.GetProperty("paths")
            .GetProperty("/api/search").GetProperty("post")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString();

        schema.Should().EndWith("/SearchResponse");
        document.RootElement.GetProperty("components").GetProperty("schemas")
            .TryGetProperty("DownloadDto", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RemoteBoundary_HidesUi_AndRejectsPlainHttp()
    {
        (await _client.GetAsync("/settings")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/settings");
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
    }

    [Fact]
    public async Task Search_RejectsUnknownSource_InsteadOfReturningAmbiguousEmptyResults()
    {
        var response = await _client.PostAsJsonAsync("/api/search", new SearchRequest("ubuntu", "Missing"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<AgentErrorDto>())!.Error.Should().Contain("Unknown source");
    }

    [Fact]
    public async Task Search_ExplainsDisabledAndUnconfiguredSources()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var settings = await db.GetSettingsAsync();
            settings.SetProviderEnabled("Integration", false);
            await db.SaveChangesAsync();
        }

        var disabled = await _client.PostAsJsonAsync(
            "/api/search", new SearchRequest("ubuntu", "Integration"));
        disabled.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await disabled.Content.ReadFromJsonAsync<AgentErrorDto>())!.Error.Should().Contain("disabled");

        var credentials = await _client.PostAsJsonAsync(
            "/api/search", new SearchRequest("ubuntu", "Credentials"));
        credentials.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await credentials.Content.ReadFromJsonAsync<AgentErrorDto>())!.Error.Should().Contain("credentials");
    }

    [Fact]
    public async Task StartDownload_RejectsASaveFolderOutsideTheDownloadRoot()
    {
        // The attack this closes: torrent filenames come from torrent metadata, so a chosen folder
        // plus a chosen torrent writes attacker-controlled content anywhere the user can write. An
        // agent picks both after reading text fetched from the internet.
        var escape = Path.Combine(_app.RootDirectory, "not-downloads", "payload");

        var response = await _client.PostAsJsonAsync("/api/downloads", new StartDownloadRequest(
            Magnet: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=probe",
            Folder: escape));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<AgentErrorDto>())!.Error.Should().Contain("outside");
        Directory.Exists(escape).Should().BeFalse("a rejected folder must not be created either");
    }

    [Fact]
    public async Task StartDownload_AcceptsASubfolderOfTheDownloadRoot()
    {
        var allowed = Path.Combine(_app.DownloadDirectory, "Shows", "Season 3");

        var response = await _client.PostAsJsonAsync("/api/downloads", new StartDownloadRequest(
            Magnet: "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234568&dn=probe",
            Folder: allowed));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<DownloadDto>())!.SavePath.Should().Be(allowed);
    }

    [Fact]
    public async Task PatchSeriesTask_ChangesOnlyWhatItNames()
    {
        var created = await (await _client.PostAsJsonAsync("/api/series", new SeriesTaskRequest(
            "Patch probe", "patch probe query", Season: 3, StartEpisode: 7,
            CheckIntervalMinutes: 720, Enabled: false)))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        // Rename only — the shape an agent produces when it means to change one field.
        var patched = await (await _client.PatchAsJsonAsync($"/api/series/{created!.Id}",
                new SeriesTaskPatch(Name: "Patch probe renamed")))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        patched!.Name.Should().Be("Patch probe renamed");
        patched.Season.Should().Be(3);
        patched.StartEpisode.Should().Be(7);
        patched.NextEpisode.Should().Be(7);
        patched.CheckIntervalMinutes.Should().Be(720);
        patched.Enabled.Should().BeFalse("a deliberately disabled rule must not re-arm itself");
    }

    [Fact]
    public async Task PatchSeriesTask_CanStillTurnARuleBackOn()
    {
        var created = await (await _client.PostAsJsonAsync("/api/series", new SeriesTaskRequest(
                "Toggle probe", "toggle probe query", Enabled: false)))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        var patched = await (await _client.PatchAsJsonAsync($"/api/series/{created!.Id}",
                new SeriesTaskPatch(Enabled: true)))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        patched!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task PatchSeriesTask_RejectsAFolderOutsideTheDownloadRoot()
    {
        var created = await (await _client.PostAsJsonAsync("/api/series", new SeriesTaskRequest(
                "Folder probe", "folder probe query")))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        var response = await _client.PatchAsJsonAsync($"/api/series/{created!.Id}",
            new SeriesTaskPatch(DownloadFolder: Path.Combine(_app.RootDirectory, "elsewhere")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_ReportsTruncationRatherThanSilentlyDroppingRows()
    {
        var response = await (await _client.PostAsJsonAsync("/api/search", new SearchRequest("ubuntu", Limit: 1)))
            .Content.ReadFromJsonAsync<SearchResponse>();

        response!.Results.Should().ContainSingle();
        response.TotalMatched.Should().BeGreaterThanOrEqualTo(1);
        // With one result and one match there is nothing to truncate; the flag must agree.
        response.Truncated.Should().Be(response.TotalMatched > response.Results.Count);
    }

    [Fact]
    public async Task PutSeriesTask_RejectsAPartialBody_InsteadOfSilentlyResettingFields()
    {
        var created = await (await _client.PostAsJsonAsync("/api/series", new SeriesTaskRequest(
                "Put probe", "put probe query", Season: 3, StartEpisode: 7,
                CheckIntervalMinutes: 720, Enabled: false)))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        // The shape that used to reset season/start/interval and re-enable the rule.
        var response = await _client.PutAsJsonAsync($"/api/series/{created!.Id}",
            new { name = "Put probe renamed", query = "put probe query" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // The message has to say which fields are missing, or a caller cannot correct itself.
        (await response.Content.ReadFromJsonAsync<AgentErrorDto>())!.Error
            .Should().Contain("startEpisode").And.Contain("season");

        var unchanged = await _client.GetFromJsonAsync<SeriesTaskDto>($"/api/series/{created.Id}");
        unchanged!.Season.Should().Be(3);
        unchanged.StartEpisode.Should().Be(7);
        unchanged.CheckIntervalMinutes.Should().Be(720);
        unchanged.Enabled.Should().BeFalse();
        unchanged.Name.Should().Be("Put probe");
    }

    [Fact]
    public async Task PutSeriesTask_ReplacesEveryField_AndCanClearOnesAPatchCannot()
    {
        var created = await (await _client.PostAsJsonAsync("/api/series", new SeriesTaskRequest(
                "Clear probe", "clear probe query", Season: 3, TitleFilter: "1080p", StartEpisode: 7)))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        // A patch reads null as "leave alone", so clearing a field is what PUT is for.
        var replaced = await (await _client.PutAsJsonAsync($"/api/series/{created!.Id}",
                new SeriesTaskReplacement
                {
                    Name = "Clear probe",
                    Query = "clear probe query",
                    Provider = null,
                    TitleFilter = null,
                    Season = null,
                    StartEpisode = 7,
                    EndEpisode = null,
                    CheckIntervalMinutes = 720,
                    Enabled = false,
                    DownloadFolder = null
                }))
            .Content.ReadFromJsonAsync<SeriesTaskDto>();

        replaced!.Season.Should().BeNull();
        replaced.TitleFilter.Should().BeNull();
        replaced.StartEpisode.Should().Be(7, "fields that were sent keep the value sent");
        replaced.CheckIntervalMinutes.Should().Be(720);
        replaced.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task MalformedJson_ReturnsTheApiErrorEnvelope_NotAnExceptionPage()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/search")
        {
            Content = new StringContent("{\"query\":", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var error = await response.Content.ReadFromJsonAsync<AgentErrorDto>();
        error!.Error.Should().NotBeNullOrWhiteSpace();
        error.Error.Should().NotContain("   at ", "a caller must never be handed a stack trace");
    }

    private sealed class TestApplication : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public string RootDirectory { get; } = Path.Combine(
            Path.GetTempPath(), "MediaDownloader-AgentApiTests", Guid.NewGuid().ToString("N"));
        public string DownloadDirectory => Path.Combine(RootDirectory, "downloads");
        public string TorrentCacheDirectory => Path.Combine(RootDirectory, "torrent-cache");

        public TestApplication() => _connection.Open();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MD_NO_TRAY", "1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_connection));

                services.RemoveAll<ITorrentSearchProvider>();
                services.AddSingleton<ITorrentSearchProvider, IntegrationTorrentProvider>();
                services.AddSingleton<ITorrentSearchProvider, CredentialTorrentProvider>();

                services.PostConfigure<DownloadEngineOptions>(options =>
                    options.CacheDirectory = TorrentCacheDirectory);

                // Run the real DownloadManager without a torrent engine, so this exercises the
                // endpoints and persistence without opening peer, DHT, tracker or port-forwarding
                // sockets.
                services.RemoveAll<ITorrentEngineFactory>();
                services.AddSingleton<ITorrentEngineFactory, NoTorrentEngineFactory>();

                // Keep only the download engine running. Series/update background polling is not
                // part of this endpoint flow and would add unrelated timers/network activity.
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
    }

    /// <summary>Runs DownloadManager with no MonoTorrent engine at all — see ITorrentEngineFactory.</summary>
    private sealed class NoTorrentEngineFactory : ITorrentEngineFactory
    {
        public MonoTorrent.Client.ClientEngine? Create() => null;
    }

    private sealed class IntegrationTorrentProvider : ITorrentSearchProvider
    {
        private const string Hash = "0123456789abcdef0123456789abcdef01234567";
        public string Name => "Integration";

        public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TorrentSearchResult>>(new[]
            {
                new TorrentSearchResult
                {
                    Title = "Ubuntu Integration Image",
                    Source = Name,
                    InfoHash = Hash,
                    MagnetUri = $"magnet:?xt=urn:btih:{Hash}&dn=Ubuntu+Integration+Image",
                    SizeBytes = 1_000_000,
                    Seeders = 10
                }
            });
    }

    private sealed class CredentialTorrentProvider : ITorrentSearchProvider
    {
        public string Name => "Credentials";
        public bool RequiresCredentials => true;

        public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(
            string query, CancellationToken ct = default) =>
            throw new InvalidOperationException("A source without credentials must never be called.");
    }
}
