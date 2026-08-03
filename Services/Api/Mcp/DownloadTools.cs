using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MediaDownloader.Services.Api.Mcp;

[McpServerToolType]
public static class DownloadTools
{
    [McpServerTool(Name = "start_download", ReadOnly = false, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Queue a torrent download. Prefer a resultId from search_torrents because lazy and private sources cannot always be started from a magnet alone.")]
    public static Task<DownloadDto> StartDownload(
        AgentApi api,
        [Description("Opaque id returned by search_torrents.")] string? resultId = null,
        [Description("Raw magnet URI when no search result id is available.")] string? magnet = null,
        [Description("Optional save folder override.")] string? folder = null,
        CancellationToken ct = default) =>
        McpToolCall.RunAsync(() => api.StartDownloadAsync(new StartDownloadRequest(resultId, magnet, folder), ct));

    [McpServerTool(Name = "list_downloads", ReadOnly = true, Destructive = false, UseStructuredContent = true)]
    [Description("List downloads with current progress, speeds, peers, and status.")]
    public static Task<IReadOnlyList<DownloadDto>> ListDownloads(
        AgentApi api,
        [Description("Optional status filter: Queued, FetchingMetadata, Downloading, Seeding, Paused, Completed, or Error.")] string? status = null) =>
        McpToolCall.RunAsync(() => Task.FromResult(api.GetDownloads(status)));

    [McpServerTool(Name = "pause_download", ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Pause an active download.")]
    public static Task<DownloadDto> PauseDownload(AgentApi api, [Description("Download id from list_downloads.")] int id) =>
        McpToolCall.RunAsync(() => api.PauseDownloadAsync(id));

    [McpServerTool(Name = "resume_download", ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Resume a paused download.")]
    public static Task<DownloadDto> ResumeDownload(AgentApi api, [Description("Download id from list_downloads.")] int id) =>
        McpToolCall.RunAsync(() => api.ResumeDownloadAsync(id));

    [McpServerTool(Name = "delete_download", ReadOnly = false, Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Remove a download from MediaDownloader. deleteFiles defaults to false. Setting it true permanently erases downloaded data from disk.")]
    public static Task<ActionResultDto> DeleteDownload(
        AgentApi api,
        [Description("Download id from list_downloads.")] int id,
        [Description("False keeps downloaded files. True permanently erases them.")] bool deleteFiles = false) =>
        McpToolCall.RunAsync(async () =>
        {
            await api.DeleteDownloadAsync(id, deleteFiles);
            return new ActionResultDto(true, deleteFiles
                ? $"Download {id} and its files were deleted."
                : $"Download {id} was removed; downloaded files were kept.");
        });
}
