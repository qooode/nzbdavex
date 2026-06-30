using NzbWebDAV.Utils;
using Xunit;

namespace NzbWebDAV.Tests.Utils;

public class HttpByteRangeParserTests
{
    [Theory]
    [InlineData("bytes=10-19", 100, 10, 19, 10, "bytes 10-19/100")]
    [InlineData("bytes=10-", 100, 10, 99, 90, "bytes 10-99/100")]
    [InlineData("bytes=-20", 100, 80, 99, 20, "bytes 80-99/100")]
    [InlineData("bytes=-200", 100, 0, 99, 100, "bytes 0-99/100")]
    [InlineData("bytes=0-0", 100, 0, 0, 1, "bytes 0-0/100")]
    [InlineData("bytes=90-200", 100, 90, 99, 10, "bytes 90-99/100")]
    public void Parse_ReturnsSatisfiableRange(
        string header,
        long totalLength,
        long expectedStart,
        long expectedEnd,
        long expectedLength,
        string expectedContentRange)
    {
        var result = HttpByteRangeParser.Parse(header, totalLength);

        Assert.Equal(HttpByteRangeStatus.Satisfiable, result.Status);
        Assert.NotNull(result.Range);
        Assert.Equal(expectedStart, result.Range.Start);
        Assert.Equal(expectedEnd, result.Range.End);
        Assert.Equal(expectedLength, result.Range.Length);
        Assert.Equal(expectedContentRange, result.ContentRange);
    }

    [Theory]
    [InlineData("bytes=100-", 100)]
    [InlineData("bytes=20-10", 100)]
    [InlineData("bytes=-0", 100)]
    [InlineData("bytes=-20", 0)]
    [InlineData("bytes=0-1,3-4", 100)]
    [InlineData("bytes=abc", 100)]
    public void Parse_ReturnsUnsatisfiableForInvalidOrImpossibleByteRanges(string header, long totalLength)
    {
        var result = HttpByteRangeParser.Parse(header, totalLength);

        Assert.Equal(HttpByteRangeStatus.Unsatisfiable, result.Status);
        Assert.Null(result.Range);
        Assert.Equal($"bytes */{totalLength}", result.ContentRange);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-10")]
    public void Parse_IgnoresMissingOrUnsupportedRangeUnits(string? header)
    {
        var result = HttpByteRangeParser.Parse(header, 100);

        Assert.Equal(HttpByteRangeStatus.NoRange, result.Status);
        Assert.Null(result.Range);
        Assert.Null(result.ContentRange);
    }
}
