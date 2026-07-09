using FluentAssertions;
using MediaDownloader.Services;

namespace MediaDownloader.Tests.Services;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1024L * 1024, "1 MiB")]
    [InlineData(1024L * 1024 * 1024, "1 GiB")]
    [InlineData((long)(1.4 * 1024 * 1024 * 1024), "1.4 GiB")]
    public void Format_ReturnsExpectedBinaryUnit(long bytes, string expected)
    {
        ByteSize.Format(bytes).Should().Be(expected);
    }

    [Fact]
    public void FormatRate_AppendsPerSecondSuffix()
    {
        ByteSize.FormatRate(1024).Should().Be("1 KiB/s");
    }

    [Theory]
    [InlineData("1.4 GiB", 1503238553)]
    [InlineData("550.3 MiB", 577031372)]
    [InlineData("1 KB", 1024)]
    [InlineData("2 MB", 2097152)]
    public void Parse_ReturnsExpectedByteCount(string text, long expected)
    {
        ByteSize.Parse(text).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("42")]
    public void Parse_ReturnsZero_ForUnparseableInput(string? text)
    {
        ByteSize.Parse(text).Should().Be(0);
    }

    [Fact]
    public void Parse_HandlesNonBreakingSpaceBetweenNumberAndUnit()
    {
        ByteSize.Parse("1.4 GiB").Should().Be(1503238553);
    }
}
