using FluentAssertions;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Tests.Services.Torrents;

public class DescriptionExtractorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void ToPlainText_ReturnsNull_ForEmptyInput(string? html)
    {
        DescriptionExtractor.ToPlainText(html).Should().BeNull();
    }

    [Fact]
    public void ToPlainText_ReturnsNull_WhenOnlyTagsRemain()
    {
        DescriptionExtractor.ToPlainText("<div></div><span> </span>").Should().BeNull();
    }

    [Fact]
    public void ToPlainText_PassesThroughPlainText()
    {
        DescriptionExtractor.ToPlainText("Just plain text").Should().Be("Just plain text");
    }

    [Theory]
    [InlineData("Line one<br>Line two", "Line one\nLine two")]
    [InlineData("Line one<br/>Line two", "Line one\nLine two")]
    [InlineData("Line one<br />Line two", "Line one\nLine two")]
    [InlineData("Line one<BR>Line two", "Line one\nLine two")]
    public void ToPlainText_ConvertsBrToNewline(string html, string expected)
    {
        DescriptionExtractor.ToPlainText(html).Should().Be(expected);
    }

    [Theory]
    [InlineData("<p>First</p><p>Second</p>", "First\nSecond")]
    [InlineData("<div>First</div><div>Second</div>", "First\nSecond")]
    [InlineData("<ul><li>First</li><li>Second</li></ul>", "First\nSecond")]
    public void ToPlainText_ConvertsBlockClosingTagsToNewline(string html, string expected)
    {
        DescriptionExtractor.ToPlainText(html).Should().Be(expected);
    }

    [Fact]
    public void ToPlainText_StripsInlineTags_WithoutInsertingNewlines()
    {
        DescriptionExtractor.ToPlainText("Some <strong>bold</strong> and <em>italic</em> text")
            .Should().Be("Some bold and italic text");
    }

    [Fact]
    public void ToPlainText_StripsTagMarkup_ForDisallowedElements()
    {
        // No raw HTML is ever rendered from this value (Razor auto-encodes plain @expressions), but
        // stripping tag markup here is still a defense-in-depth layer against script injection.
        var result = DescriptionExtractor.ToPlainText("<script>alert(1)</script>Hello");

        result.Should().NotContain("<script>");
        result.Should().NotContain("</script>");
        result.Should().Contain("alert(1)");
        result.Should().Contain("Hello");
    }

    [Fact]
    public void ToPlainText_DecodesHtmlEntities()
    {
        DescriptionExtractor.ToPlainText("Tom &amp; Jerry &lt;3 &quot;fun&quot; &#39;times&#39;")
            .Should().Be("Tom & Jerry <3 \"fun\" 'times'");
    }

    [Fact]
    public void ToPlainText_CollapsesThreeOrMoreNewlines_ToTwo()
    {
        DescriptionExtractor.ToPlainText("A<br><br><br><br>B").Should().Be("A\n\nB");
    }

    [Fact]
    public void ToPlainText_TrimsLeadingAndTrailingWhitespace()
    {
        DescriptionExtractor.ToPlainText("<p></p>  Hello  <p></p>").Should().Be("Hello");
    }

    [Fact]
    public void ToPlainText_HandlesRealisticScrapedDescription()
    {
        const string html = """
            <p><img src="https://distrowatch.com/images/x/ubuntumate.png" class="img-responsive"><br><br>Ubuntu MATE is a desktop Linux distribution. <br><br><strong>OS Type </strong> -&gt; Linux
            <br><strong>Based on </strong> -&gt; Debian, Ubuntu</p>
            """;

        var result = DescriptionExtractor.ToPlainText(html);

        result.Should().NotBeNull();
        result.Should().NotContain("<img");
        result.Should().NotContain("<strong>");
        result.Should().Contain("Ubuntu MATE is a desktop Linux distribution.");
        result.Should().Contain("OS Type  -> Linux");
    }
}
