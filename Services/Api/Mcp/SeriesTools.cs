using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MediaDownloader.Services.Api.Mcp;

[McpServerToolType]
public static class SeriesTools
{
    [McpServerTool(Name = "list_series_tasks", ReadOnly = true, Destructive = false, UseStructuredContent = true)]
    [Description("List automatic series download rules and their next episode/check state.")]
    public static Task<IReadOnlyList<SeriesTaskDto>> ListSeriesTasks(AgentApi api, CancellationToken ct) =>
        McpToolCall.RunAsync(() => api.GetSeriesTasksAsync(ct));

    [McpServerTool(Name = "create_series_task", ReadOnly = false, Destructive = false, UseStructuredContent = true)]
    [Description("Create an automatic rule that searches for and downloads new episodes.")]
    public static Task<SeriesTaskDto> CreateSeriesTask(
        AgentApi api, string name, string query, string? provider = null, string? titleFilter = null,
        int? season = null, int startEpisode = 1, int? endEpisode = null,
        int checkIntervalMinutes = 60, bool enabled = true, string? downloadFolder = null,
        CancellationToken ct = default) =>
        McpToolCall.RunAsync(() => api.CreateSeriesTaskAsync(new SeriesTaskRequest(
            name, query, provider, titleFilter, season, startEpisode, endEpisode,
            checkIntervalMinutes, enabled, downloadFolder), ct));

    [McpServerTool(Name = "update_series_task", ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Change one or more fields of an automatic series rule. Anything you leave out keeps its current value, so pass only what should change.")]
    public static Task<SeriesTaskDto> UpdateSeriesTask(
        AgentApi api,
        [Description("Series task id from list_series_tasks.")] int id,
        string? name = null, string? query = null, string? provider = null,
        [Description("Extra text that must appear in a result title.")] string? titleFilter = null,
        int? season = null,
        [Description("First episode to look for.")] int? startEpisode = null,
        [Description("Last episode; the rule disables itself once it is downloaded.")] int? endEpisode = null,
        int? checkIntervalMinutes = null,
        [Description("False pauses the rule without deleting it.")] bool? enabled = null,
        [Description("Must be inside the configured download folder.")] string? downloadFolder = null,
        CancellationToken ct = default) =>
        McpToolCall.RunAsync(() => api.PatchSeriesTaskAsync(id, new SeriesTaskPatch(
            name, query, provider, titleFilter, season, startEpisode, endEpisode,
            checkIntervalMinutes, enabled, downloadFolder), ct));

    [McpServerTool(Name = "delete_series_task", ReadOnly = false, Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Delete an automatic series rule. Existing downloads and their files are kept.")]
    public static Task<ActionResultDto> DeleteSeriesTask(AgentApi api, int id, CancellationToken ct = default) =>
        McpToolCall.RunAsync(async () =>
        {
            await api.DeleteSeriesTaskAsync(id, ct);
            return new ActionResultDto(true, $"Series task {id} was deleted.");
        });

    [McpServerTool(Name = "check_series_task_now", ReadOnly = false, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Run one series rule immediately. This can queue one or more matching episode downloads.")]
    public static Task<SeriesTaskDto> CheckSeriesTaskNow(AgentApi api, int id, CancellationToken ct = default) =>
        McpToolCall.RunAsync(() => api.CheckSeriesTaskNowAsync(id, ct));
}
