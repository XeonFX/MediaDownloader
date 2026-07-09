using FluentAssertions;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Tests.Services.Torrents;

public class SearchRelevanceTests
{
    [Theory]
    [InlineData("ubuntu 24.04", "ubuntu-24.04-desktop-amd64.iso")]
    [InlineData("The Matrix 1999", "The.Matrix.1999.1080p.BluRay.x264")]
    public void Matches_ReturnsTrue_WhenEveryQueryTokenIsInTitle(string query, string title)
    {
        SearchRelevance.Matches(query, title).Should().BeTrue();
    }

    [Fact]
    public void Matches_ReturnsFalse_WhenATokenIsMissing()
    {
        SearchRelevance.Matches("ubuntu 24.04", "Debian 12 netinst").Should().BeFalse();
    }

    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        SearchRelevance.Matches("UBUNTU", "totally-ubuntu-iso").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public void Matches_ReturnsTrue_WhenQueryHasNoMeaningfulTokens(string query)
    {
        // Tokens shorter than 2 chars are dropped; an empty token set means "don't filter".
        SearchRelevance.Matches(query, "anything at all").Should().BeTrue();
    }

    [Fact]
    public void Matches_IgnoresPunctuationDifferences()
    {
        SearchRelevance.Matches("show name s01e05", "Show.Name.S01E05.720p").Should().BeTrue();
    }

    [Fact]
    public void Matches_DeduplicatesRepeatedTokens()
    {
        SearchRelevance.Matches("ubuntu ubuntu", "the ubuntu iso").Should().BeTrue();
    }
}
