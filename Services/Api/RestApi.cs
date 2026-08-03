namespace MediaDownloader.Services.Api;

/// <summary>Minimal REST surface over <see cref="AgentApi"/>. All behavior remains in the facade.</summary>
public static class RestApi
{
    public static RouteGroupBuilder MapAgentApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api")
            .WithTags("Agent API");

        api.MapGet("/sources", (AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.GetSourcesAsync(ct)))
            .WithName("ListSources")
            .WithSummary("List torrent search sources and their availability")
            .Produces<SourceDto[]>();

        api.MapPost("/search", (SearchRequest request, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.SearchAsync(request, ct)))
            .WithName("SearchTorrents")
            .WithSummary("Search enabled torrent sources")
            .Produces<SearchResponse>()
            .WithAgentErrors();

        api.MapGet("/search/{resultId}", (string resultId, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.GetTorrentDetailsAsync(resultId, ct)))
            .WithName("GetTorrentDetails")
            .WithSummary("Resolve full details for a cached search result")
            .Produces<TorrentDetailsDto>()
            .WithAgentErrors();

        api.MapPost("/downloads", (StartDownloadRequest request, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.StartDownloadAsync(request, ct), StatusCodes.Status201Created))
            .WithName("StartDownload")
            .WithSummary("Queue a download from a result id or magnet URI")
            .Produces<DownloadDto>(StatusCodes.Status201Created)
            .WithAgentErrors();

        api.MapGet("/downloads", (string? status, AgentApi agent) =>
                Execute(() => agent.GetDownloads(status)))
            .WithName("ListDownloads")
            .WithSummary("List downloads with live progress")
            .Produces<DownloadDto[]>()
            .WithAgentErrors();

        api.MapGet("/downloads/{id:int}", (int id, AgentApi agent) =>
                Execute(() => agent.GetDownload(id)))
            .WithName("GetDownload")
            .WithSummary("Get one download with live progress")
            .Produces<DownloadDto>()
            .WithAgentErrors();

        api.MapPost("/downloads/{id:int}/pause", (int id, AgentApi agent) =>
                ExecuteAsync(() => agent.PauseDownloadAsync(id)))
            .WithName("PauseDownload")
            .Produces<DownloadDto>()
            .WithAgentErrors();

        api.MapPost("/downloads/{id:int}/resume", (int id, AgentApi agent) =>
                ExecuteAsync(() => agent.ResumeDownloadAsync(id)))
            .WithName("ResumeDownload")
            .Produces<DownloadDto>()
            .WithAgentErrors();

        api.MapDelete("/downloads/{id:int}", (int id, bool? deleteFiles, AgentApi agent) =>
                ExecuteAsync(async () =>
                {
                    var eraseFiles = deleteFiles ?? false;
                    await agent.DeleteDownloadAsync(id, eraseFiles);
                    return new ActionResultDto(true, eraseFiles
                        ? $"Download {id} and its files were deleted."
                        : $"Download {id} was removed; downloaded files were kept.");
                }))
            .WithName("DeleteDownload")
            .WithSummary("Remove a download; deleteFiles defaults to false")
            .Produces<ActionResultDto>()
            .WithAgentErrors();

        api.MapGet("/series", (AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.GetSeriesTasksAsync(ct)))
            .WithName("ListSeriesTasks")
            .Produces<SeriesTaskDto[]>();

        api.MapGet("/series/{id:int}", (int id, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.GetSeriesTaskAsync(id, ct)))
            .WithName("GetSeriesTask")
            .Produces<SeriesTaskDto>()
            .WithAgentErrors();

        api.MapPost("/series", (SeriesTaskRequest request, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.CreateSeriesTaskAsync(request, ct), StatusCodes.Status201Created))
            .WithName("CreateSeriesTask")
            .Produces<SeriesTaskDto>(StatusCodes.Status201Created)
            .WithAgentErrors();

        api.MapPut("/series/{id:int}", (int id, SeriesTaskReplacement replacement, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.UpdateSeriesTaskAsync(id, replacement, ct)))
            .WithName("UpdateSeriesTask")
            .WithSummary("Replace a series task in full; every field is required. Use PATCH to change a subset.")
            .Produces<SeriesTaskDto>()
            .WithAgentErrors();

        api.MapPatch("/series/{id:int}", (int id, SeriesTaskPatch patch, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.PatchSeriesTaskAsync(id, patch, ct)))
            .WithName("PatchSeriesTask")
            .WithSummary("Change some fields of a series task; omitted fields keep their current value")
            .Produces<SeriesTaskDto>()
            .WithAgentErrors();

        api.MapDelete("/series/{id:int}", (int id, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(async () =>
                {
                    await agent.DeleteSeriesTaskAsync(id, ct);
                    return new ActionResultDto(true, $"Series task {id} was deleted.");
                }))
            .WithName("DeleteSeriesTask")
            .Produces<ActionResultDto>()
            .WithAgentErrors();

        api.MapPost("/series/{id:int}/check", (int id, AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.CheckSeriesTaskNowAsync(id, ct)))
            .WithName("CheckSeriesTaskNow")
            .Produces<SeriesTaskDto>()
            .WithAgentErrors();

        api.MapGet("/settings", (AgentApi agent, CancellationToken ct) =>
                ExecuteAsync(() => agent.GetSettingsAsync(ct)))
            .WithName("GetAgentSettings")
            .WithSummary("Read the non-secret settings relevant to downloads")
            .Produces<SettingsDto>();

        return api;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> action, int statusCode = StatusCodes.Status200OK)
    {
        try
        {
            return Results.Json(await action(), statusCode: statusCode);
        }
        catch (AgentApiException ex)
        {
            return Error(ex);
        }
    }

    private static IResult Execute<T>(Func<T> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (AgentApiException ex)
        {
            return Error(ex);
        }
    }

    private static IResult Error(AgentApiException ex) =>
        Results.Json(new AgentErrorDto(ex.Message), statusCode: ex.StatusCode);

    private static RouteHandlerBuilder WithAgentErrors(this RouteHandlerBuilder builder) => builder
        .Produces<AgentErrorDto>(StatusCodes.Status400BadRequest)
        .Produces<AgentErrorDto>(StatusCodes.Status404NotFound);
}
