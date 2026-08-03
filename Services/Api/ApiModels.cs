using System.ComponentModel;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Services.Api;

// Plain records shared by the REST endpoints and the MCP tools. Deliberately separate from the
// domain types: TorrentSearchResult carries mutable, lazily-resolved fields and DownloadItem is an
// EF entity with [NotMapped] live stats and a navigation property — neither is safe or stable to
// serialise straight out to a caller. The [Description] attributes are what an MCP client shows the
// model when it inspects a tool's schema, so they are written for that audience.

/// <summary>A search source and whether the user has it switched on.</summary>
public record SourceDto(
    [property: Description("Source name, e.g. \"Nyaa\". Pass this as the `source` argument to search one site only.")]
    string Name,
    [property: Description("False when the user has disabled this source; disabled sources are skipped by searches.")]
    bool Enabled,
    [property: Description("True when the source needs an account that has not been configured, so searches skip it.")]
    bool NeedsCredentials);

/// <summary>One row of a search.</summary>
public record SearchResultDto(
    [property: Description("Opaque handle for this result. Pass it to start_download or get_torrent_details. Valid for about 30 minutes.")]
    string ResultId,
    string Title,
    [property: Description("Which source this came from.")]
    string Source,
    long SizeBytes,
    [property: Description("Human-readable size, e.g. \"1.4 GiB\".")]
    string Size,
    int Seeders,
    int Leechers,
    [property: Description("When the torrent was published, in UTC. Null when the source doesn't say.")]
    DateTime? PublishedAt,
    [property: Description("Page for this torrent on the source site, when known.")]
    string? DetailsUrl);

/// <summary>How one source fared during a search.</summary>
public record SourceOutcomeDto(
    string Source,
    [property: Description("\"ok\" when the source answered, \"failed\" when it could not be reached or returned something unusable.")]
    string Status,
    [property: Description("Rows the source returned before relevance filtering.")]
    int Returned,
    [property: Description("Rows dropped because the title didn't contain every word of the query.")]
    int Filtered,
    [property: Description("Why the source failed; null when it succeeded.")]
    string? Error);

/// <summary>
/// A completed search. Always check <see cref="Sources"/> before concluding that nothing exists:
/// an empty <see cref="Results"/> with a failed source means that site could not be reached, which
/// is a different thing from the query genuinely having no matches.
/// </summary>
public record SearchResponse(
    IReadOnlyList<SearchResultDto> Results,
    [property: Description("Per-source outcome, including sources that failed or returned nothing.")]
    IReadOnlyList<SourceOutcomeDto> Sources,
    [property: Description("Distinct torrents matched across all sources, before `limit` was applied.")]
    int TotalMatched,
    [property: Description("True when `limit` cut the list short — raise `limit` to see more. The rows you did get are the highest-seeded ones.")]
    bool Truncated);

/// <summary>Full detail for one search result, including the description when the source has one.</summary>
public record TorrentDetailsDto(
    SearchResultDto Result,
    [property: Description("Description or release notes from the source site. Null when it has none.")]
    string? Description,
    [property: Description("Magnet link, once resolved. Null for private trackers, which serve .torrent files instead.")]
    string? MagnetUri);

/// <summary>A download tracked by the engine.</summary>
public record DownloadDto(
    int Id,
    string Name,
    [property: Description("One of: Queued, FetchingMetadata, Downloading, Seeding, Paused, Completed, Error.")]
    string Status,
    [property: Description("Percentage complete, 0-100.")]
    double Progress,
    long TotalBytes,
    [property: Description("Current download rate in bytes per second; 0 unless actively downloading.")]
    long DownloadSpeed,
    long UploadSpeed,
    [property: Description("Connected peers right now.")]
    int Peers,
    [property: Description("Which search source this came from.")]
    string Source,
    [property: Description("Folder the files are being written to.")]
    string SavePath,
    DateTime AddedAt,
    DateTime? CompletedAt,
    [property: Description("Failure reason when Status is Error; null otherwise.")]
    string? Error,
    [property: Description("Id of the series task that queued this, when it wasn't started by hand.")]
    int? SeriesTaskId)
{
    public static DownloadDto From(DownloadItem item) => new(
        item.Id, item.Name, item.Status.ToString(), item.Progress, item.TotalBytes,
        item.DownloadSpeed, item.UploadSpeed, item.Peers, item.Source, item.SavePath,
        item.AddedAt, item.CompletedAt, item.Error, item.SeriesTaskId);
}

/// <summary>An automatic-download rule for a show.</summary>
public record SeriesTaskDto(
    int Id,
    string Name,
    [property: Description("Search query used to find episodes, e.g. \"One Piece 1080p\".")]
    string Query,
    [property: Description("Restrict to one source, or null to search all of them.")]
    string? Provider,
    [property: Description("Extra text that must appear in a result's title, e.g. a release group.")]
    string? TitleFilter,
    int? Season,
    int StartEpisode,
    int? EndEpisode,
    [property: Description("Highest episode already downloaded; 0 when nothing has been yet.")]
    int LastDownloadedEpisode,
    [property: Description("Next episode this task will look for.")]
    int NextEpisode,
    int CheckIntervalMinutes,
    bool Enabled,
    string? DownloadFolder,
    DateTime? LastCheckedAt)
{
    public static SeriesTaskDto From(SeriesTask t) => new(
        t.Id, t.Name, t.Query, t.Provider, t.TitleFilter, t.Season, t.StartEpisode, t.EndEpisode,
        t.LastDownloadedEpisode, t.NextEpisode, t.CheckIntervalMinutes, t.Enabled, t.DownloadFolder,
        t.LastCheckedAt);
}

/// <summary>Read-only view of the settings an agent has any business knowing about.</summary>
public record SettingsDto(
    [property: Description("Default folder downloads are saved to.")]
    string DownloadFolder,
    [property: Description("What happens when a download finishes: StopSeeding or KeepSeeding.")]
    string PostDownloadAction);

/// <summary>Acknowledgement returned by agent commands that intentionally have no resource body.</summary>
public record ActionResultDto(bool Success, string Message);

/// <summary>Stable error envelope returned by the REST API.</summary>
public record AgentErrorDto(string Error);

// ---- Requests ----

public record SearchRequest(
    string Query,
    [property: Description("Search only this source (see list_sources). Omit to search all enabled sources.")]
    string? Source = null,
    [property: Description("Maximum rows to return, highest-seeded first. Defaults to 25.")]
    int? Limit = null);

/// <summary>
/// Either <see cref="ResultId"/> (from a search) or <see cref="Magnet"/> must be given. Prefer the
/// result id: for sources that resolve magnets lazily, or private trackers that serve .torrent
/// files, it is the only way to start the download.
/// </summary>
public record StartDownloadRequest(
    string? ResultId = null,
    [property: Description("A magnet URI to add directly, when you already have one.")]
    string? Magnet = null,
    [property: Description("Save to this folder instead of the default from settings.")]
    string? Folder = null);

/// <summary>
/// A complete series rule. Every field is replaced, so omitting one resets it to the default shown
/// here — use <see cref="SeriesTaskPatch"/> to change a subset.
/// </summary>
public record SeriesTaskRequest(
    string Name,
    string Query,
    string? Provider = null,
    string? TitleFilter = null,
    int? Season = null,
    int StartEpisode = 1,
    int? EndEpisode = null,
    int CheckIntervalMinutes = 60,
    bool Enabled = true,
    string? DownloadFolder = null);

/// <summary>
/// A complete replacement for an existing series rule, where every field must be stated —
/// explicitly including the ones that may be null.
///
/// Separate from <see cref="SeriesTaskRequest"/> and its defaults on purpose. Defaults are right
/// when *creating* a rule, but on a replace they turn an omitted field into a silent reset: leaving
/// out <c>enabled</c> re-armed a rule the user had switched off, and leaving out
/// <c>startEpisode</c> sent it back to episode 1, re-downloading everything it already had. With
/// <c>required</c> members, System.Text.Json refuses the payload instead, so a partial body is a
/// clear error rather than quiet data loss.
///
/// This is also the only way to clear a field: <see cref="SeriesTaskPatch"/> reads null as "leave
/// alone", so setting a season back to "no season" has to happen here.
/// </summary>
public record SeriesTaskReplacement
{
    public required string Name { get; init; }
    public required string Query { get; init; }
    public required string? Provider { get; init; }
    public required string? TitleFilter { get; init; }
    public required int? Season { get; init; }
    public required int StartEpisode { get; init; }
    public required int? EndEpisode { get; init; }
    public required int CheckIntervalMinutes { get; init; }
    public required bool Enabled { get; init; }
    public required string? DownloadFolder { get; init; }

    internal SeriesTaskRequest ToRequest() => new(
        Name, Query, Provider, TitleFilter, Season, StartEpisode, EndEpisode,
        CheckIntervalMinutes, Enabled, DownloadFolder);
}

/// <summary>
/// A partial change to a series rule: every field is optional, and anything left null keeps its
/// current value.
///
/// This exists because the full-replace form is a trap for a caller that only means to change one
/// thing — omitting the rest silently reset the season, start episode and interval, and flipped a
/// deliberately-disabled rule back on at episode 1.
/// </summary>
public record SeriesTaskPatch(
    string? Name = null,
    string? Query = null,
    string? Provider = null,
    string? TitleFilter = null,
    int? Season = null,
    int? StartEpisode = null,
    int? EndEpisode = null,
    int? CheckIntervalMinutes = null,
    bool? Enabled = null,
    string? DownloadFolder = null);

/// <summary>
/// Thrown by <see cref="AgentApi"/> for a caller mistake — an unknown id, an expired result handle,
/// a request missing a required field. Mapped to HTTP 400/404 by the REST layer and to a plain
/// error message by the MCP tools, so neither surface leaks a stack trace for an ordinary mistake.
/// </summary>
public class AgentApiException(string message, int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static AgentApiException NotFound(string message) =>
        new(message, StatusCodes.Status404NotFound);
}
