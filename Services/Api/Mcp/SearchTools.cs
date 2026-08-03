using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MediaDownloader.Services.Api.Mcp;

[McpServerToolType]
public static class SearchTools
{
    [McpServerTool(Name = "list_sources", ReadOnly = true, Destructive = false, UseStructuredContent = true)]
    [Description("List torrent sources, whether each is enabled, and whether required credentials are missing.")]
    public static Task<IReadOnlyList<SourceDto>> ListSources(AgentApi api, CancellationToken ct) =>
        McpToolCall.RunAsync(() => api.GetSourcesAsync(ct));

    [McpServerTool(Name = "search_torrents", ReadOnly = true, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Search one or all enabled torrent sources. Inspect the per-source outcomes before treating an empty result list as no matches. Result ids remain valid for about 30 minutes.")]
    public static Task<SearchResponse> SearchTorrents(
        AgentApi api,
        [Description("Words that every returned title should contain.")] string query,
        [Description("Exact source name from list_sources, or omit to search all enabled sources.")] string? source = null,
        [Description("Maximum results to return, from 1 to 200. Defaults to 25.")] int limit = 25,
        CancellationToken ct = default) =>
        McpToolCall.RunAsync(() => api.SearchAsync(new SearchRequest(query, source, limit), ct));

    [McpServerTool(Name = "get_torrent_details", ReadOnly = true, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Resolve the description and magnet for a result returned by search_torrents. Some sources fetch a detail page lazily.")]
    public static Task<TorrentDetailsDto> GetTorrentDetails(
        AgentApi api,
        [Description("Opaque result id returned by search_torrents.")] string resultId,
        CancellationToken ct = default) =>
        McpToolCall.RunAsync(() => api.GetTorrentDetailsAsync(resultId, ct));

    [McpServerTool(Name = "get_settings", ReadOnly = true, Destructive = false, UseStructuredContent = true)]
    [Description("Read the default download folder and post-download behavior. Secret and notification settings are never exposed.")]
    public static Task<SettingsDto> GetSettings(AgentApi api, CancellationToken ct) =>
        McpToolCall.RunAsync(() => api.GetSettingsAsync(ct));
}
