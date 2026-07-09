using FluentAssertions;
using MediaDownloader.Data.Entities;
using MediaDownloader.Services.Series;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Tests.Services.Series;

/// <summary>
/// Covers SeriesMonitor.MatchesTask, the logic that decides whether a search result is a genuine
/// hit for a series task's wanted episode — the most concurrency/correctness-sensitive matching
/// code in the auto-download path, previously untested.
/// </summary>
public class SeriesMonitorMatchesTaskTests
{
    private static SeriesTask Task(string query, string? titleFilter = null, int? season = null) => new()
    {
        Name = "Test Show",
        Query = query,
        TitleFilter = titleFilter,
        Season = season
    };

    private static TorrentSearchResult Result(string title) => new() { Title = title, Source = "Test" };

    [Fact]
    public void ReturnsTrue_WhenTitleContainsQueryAndEpisodeMatches()
    {
        var task = Task("Show Name", season: 1);
        var result = Result("Show.Name.S01E05.1080p.WEB-DL");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeTrue();
    }

    [Fact]
    public void ReturnsFalse_WhenEpisodeNumberDoesNotMatch()
    {
        var task = Task("Show Name", season: 1);
        var result = Result("Show.Name.S01E05.1080p.WEB-DL");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 6).Should().BeFalse();
    }

    [Fact]
    public void ReturnsFalse_WhenSeasonDoesNotMatch()
    {
        var task = Task("Show Name", season: 2);
        var result = Result("Show.Name.S01E05.1080p.WEB-DL");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeFalse();
    }

    [Fact]
    public void ReturnsFalse_WhenTaskRequiresSeasonButTitleHasNone()
    {
        var task = Task("Show Name", season: 1);
        // Anime-dash style has no season marker at all.
        var result = Result("Show Name - 05 [1080p]");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeFalse();
    }

    [Fact]
    public void ReturnsTrue_WhenNoSeasonRequired_AndTitleHasNoSeasonMarker()
    {
        var task = Task("Show Name"); // no Season set
        var result = Result("Show Name - 05 [1080p]");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeTrue();
    }

    [Fact]
    public void ReturnsFalse_WhenAQueryTokenIsMissingFromTitle()
    {
        var task = Task("Show Name", season: 1);
        var result = Result("Different.Show.S01E05.1080p");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeFalse();
    }

    [Fact]
    public void ReturnsFalse_WhenTitleFilterTokenIsMissing()
    {
        var task = Task("Show Name", titleFilter: "1080p", season: 1);
        var result = Result("Show.Name.S01E05.720p.WEB-DL");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeFalse();
    }

    [Fact]
    public void ReturnsTrue_WhenTitleFilterTokenIsPresent()
    {
        var task = Task("Show Name", titleFilter: "1080p", season: 1);
        var result = Result("Show.Name.S01E05.1080p.WEB-DL");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeTrue();
    }

    [Fact]
    public void ReturnsFalse_WhenTitleHasNoRecognizableEpisodeMarker()
    {
        var task = Task("Show Name");
        var result = Result("Show Name Complete Pack 1080p");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 1).Should().BeFalse();
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var task = Task("show name", season: 1);
        var result = Result("SHOW.NAME.S01E05.1080p");

        SeriesMonitor.MatchesTask(result, task, wantedEpisode: 5).Should().BeTrue();
    }
}
