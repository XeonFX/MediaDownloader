using FluentAssertions;
using MediaDownloader.Services.Series;

namespace MediaDownloader.Tests.Services.Series;

public class EpisodeParserTests
{
    [Theory]
    [InlineData("Show.Name.S01E05.1080p.WEB-DL", 1, 5)]
    [InlineData("Show Name S02E12", 2, 12)]
    [InlineData("show.name.s1e5.720p", 1, 5)]
    public void TryParse_MatchesStandardSxxExxFormat(string title, int expectedSeason, int expectedEpisode)
    {
        var success = EpisodeParser.TryParse(title, out var season, out var episode);

        success.Should().BeTrue();
        season.Should().Be(expectedSeason);
        episode.Should().Be(expectedEpisode);
    }

    [Theory]
    [InlineData("Show Name 1x05", 1, 5)]
    [InlineData("Show.Name.2x12.HDTV", 2, 12)]
    public void TryParse_MatchesCrossFormat(string title, int expectedSeason, int expectedEpisode)
    {
        var success = EpisodeParser.TryParse(title, out var season, out var episode);

        success.Should().BeTrue();
        season.Should().Be(expectedSeason);
        episode.Should().Be(expectedEpisode);
    }

    [Theory]
    [InlineData("Show Name Episode 5", 5)]
    [InlineData("Show Name Ep05", 5)]
    [InlineData("Show Name E05", 5)]
    public void TryParse_MatchesEpisodeWordFormat_WithNoSeason(string title, int expectedEpisode)
    {
        var success = EpisodeParser.TryParse(title, out var season, out var episode);

        success.Should().BeTrue();
        season.Should().BeNull();
        episode.Should().Be(expectedEpisode);
    }

    [Theory]
    [InlineData("Some Anime Show - 05 [1080p]", 5)]
    [InlineData("Some Anime Show - 123 [720p]", 123)]
    public void TryParse_MatchesAnimeDashFormat(string title, int expectedEpisode)
    {
        var success = EpisodeParser.TryParse(title, out var season, out var episode);

        success.Should().BeTrue();
        season.Should().BeNull();
        episode.Should().Be(expectedEpisode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_ReturnsFalse_ForEmptyTitle(string? title)
    {
        var success = EpisodeParser.TryParse(title!, out _, out _);

        success.Should().BeFalse();
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenNothingMatches()
    {
        var success = EpisodeParser.TryParse("Just a regular movie title 2024", out var season, out var episode);

        success.Should().BeFalse();
        season.Should().BeNull();
        episode.Should().Be(0);
    }

    [Fact]
    public void TryParse_PrefersSeasonEpisodeFormat_OverAnimeDash()
    {
        var success = EpisodeParser.TryParse("Show Name - S01E05 [1080p]", out var season, out var episode);

        success.Should().BeTrue();
        season.Should().Be(1);
        episode.Should().Be(5);
    }
}
