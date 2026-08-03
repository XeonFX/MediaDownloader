using FluentAssertions;
using FluentAssertions.Execution;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.Fixtures;

namespace MediaDownloader.Tests.Services.Torrents;

/// <summary>
/// Runs every parser against a full, unedited page captured from the live site and asserts the
/// invariants a healthy parse must satisfy.
///
/// The other parsing tests use small hand-written fixtures that isolate one behaviour each. That is
/// what let the Nyaa comments-link bug through: the fixture had a single row with no comments link,
/// so the markup that broke the parser simply wasn't present in any test. These fixtures are the
/// real thing, messy bits included (the Nyaa page below has 31 rows with comment links), and the
/// assertions are deliberately structural rather than exact — they should keep passing when a site
/// re-ranks its results, and start failing when a selector stops matching.
/// </summary>
public class ProviderParserSanityTests
{
    // Theory data is the provider key only, never the parsed rows: xUnit renders every argument
    // into the test name, and a list of 75 results makes a failure message unreadable.
    public static TheoryData<string> AllParsers() =>
        ["Nyaa", "The Pirate Bay (api)", "The Pirate Bay (mirror)", "EZTV", "RARBG", "1337x"];

    private static IReadOnlyList<TorrentSearchResult> Parse(string provider) => provider switch
    {
        "Nyaa" => NyaaProvider.ParseRows(FixtureLoader.Load("live-nyaa-search.html")),
        "The Pirate Bay (api)" => PirateBayProvider.ParseApi(FixtureLoader.Load("live-piratebay-api.json")),
        "The Pirate Bay (mirror)" => PirateBayProvider.ParseMirrorHtml(FixtureLoader.Load("live-piratebay-mirror.html")),
        "EZTV" => EztvProvider.ParsePage(FixtureLoader.Load("live-eztv-api.json")).Torrents,
        "RARBG" => RarbgProvider.ParsePage(FixtureLoader.Load("live-rarbg-search.json")).Results,
        "1337x" => LeetxProvider.ParseRows(FixtureLoader.Load("live-leetx-search.html"))
            .Select(r => LeetxProvider.ToResult("www.1377x.to", r)).ToList(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No fixture wired up")
    };

    /// <summary>Provider display name each parser stamps on its results.</summary>
    private static string SourceNameOf(string provider) => provider.Split(' ')[0] switch
    {
        "The" => "The Pirate Bay",
        var name => name
    };

    [Theory]
    [MemberData(nameof(AllParsers))]
    public void ParsesARepresentativeNumberOfRows(string provider)
    {
        var results = Parse(provider);

        // A broken selector reads as an empty (or nearly empty) page rather than an exception, which
        // is exactly why this needs asserting: a search page always has more than a handful of rows.
        results.Should().HaveCountGreaterThan(10, $"{provider} should parse a full results page");
    }

    [Theory]
    [MemberData(nameof(AllParsers))]
    public void ProducesTitlesThatLookLikeReleaseNames(string provider)
    {
        var results = Parse(provider);
        using var _ = new AssertionScope(provider);

        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Title));

        // The Nyaa regression in one assertion: the parser was picking up the comments link, whose
        // text is a comment count, so real titles came through as "3", "10", "8".
        results.Should().NotContain(r => r.Title.All(char.IsDigit),
            "a bare number means the parser grabbed the wrong element, not a title");

        // Titles were also never single characters or fragments of markup.
        results.Should().OnlyContain(r => r.Title.Trim().Length > 3);
        results.Should().NotContain(r => r.Title.Contains('<') || r.Title.Contains("&nbsp"));
    }

    [Theory]
    [MemberData(nameof(AllParsers))]
    public void ProducesAUsableIdentityForEveryRow(string provider)
    {
        var results = Parse(provider);
        using var _ = new AssertionScope(provider);

        // Either a real info hash (download can start straight away) or a stable placeholder that
        // cross-provider de-duplication can still key on.
        results.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.InfoHash));
        results.Should().OnlyContain(r => r.IsRealInfoHash || r.InfoHash.Contains('-'));
        var expectedSource = SourceNameOf(provider);
        results.Should().OnlyContain(r => r.Source == expectedSource);
    }

    [Theory]
    [MemberData(nameof(AllParsers))]
    public void ProducesPlausibleSizesAndPeerCounts(string provider)
    {
        var results = Parse(provider);
        using var _ = new AssertionScope(provider);

        // Sizes are the field most likely to silently become 0 when a column moves.
        results.Count(r => r.SizeBytes > 0).Should().BeGreaterThan(results.Count / 2,
            "most rows should have parsed a size");
        results.Should().OnlyContain(r => r.SizeBytes >= 0);
        results.Should().OnlyContain(r => r.Seeders >= 0 && r.Leechers >= 0);
    }

    [Theory]
    [MemberData(nameof(AllParsers))]
    public void DatesAreUtcAndWithinAPlausibleRange(string provider)
    {
        var results = Parse(provider);
        using var _ = new AssertionScope(provider);

        var dated = results.Where(r => r.PublishedAt.HasValue).Select(r => r.PublishedAt!.Value).ToList();
        dated.Should().NotBeEmpty("at least some rows should carry a publication date");

        // Mixing Kinds used to shift 1337x/RARBG dates by the local UTC offset in the UI, which can
        // move a date by a whole day for viewers west of UTC.
        dated.Should().OnlyContain(d => d.Kind == DateTimeKind.Utc);
        dated.Should().OnlyContain(d => d.Year >= 2000 && d <= DateTime.UtcNow.AddDays(2));
    }

    [Fact]
    public void NyaaKeepsTitlesOnRowsThatHaveComments()
    {
        // 31 of the 75 rows in this capture carry a "/view/<id>#comments" link ahead of the title
        // link. Before the fix, each of those parsed as its comment count and was then discarded by
        // the relevance filter — five of six episodes vanishing from a search that looked fine.
        var results = NyaaProvider.ParseRows(FixtureLoader.Load("live-nyaa-search.html"));

        results.Should().HaveCount(75);
        results.Should().OnlyContain(r => r.Title.Contains('[') || r.Title.Contains('.'));
        results.Should().OnlyContain(r => r.DetailsUrl!.StartsWith("https://nyaa.si/view/"));
        results.Should().NotContain(r => r.DetailsUrl!.Contains('#'));
    }
}
