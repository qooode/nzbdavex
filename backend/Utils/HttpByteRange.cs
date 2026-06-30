using System.Globalization;

namespace NzbWebDAV.Utils;

public enum HttpByteRangeStatus
{
    NoRange,
    Satisfiable,
    Unsatisfiable
}

public sealed record HttpByteRange(long Start, long End, long TotalLength)
{
    public long Length => End - Start + 1;
    public string ContentRange => $"bytes {Start}-{End}/{TotalLength}";
}

public sealed record HttpByteRangeResult(HttpByteRangeStatus Status, HttpByteRange? Range, string? ContentRange)
{
    public static HttpByteRangeResult NoRange { get; } = new(HttpByteRangeStatus.NoRange, null, null);

    public static HttpByteRangeResult Satisfiable(HttpByteRange range) =>
        new(HttpByteRangeStatus.Satisfiable, range, range.ContentRange);

    public static HttpByteRangeResult Unsatisfiable(long totalLength) =>
        new(HttpByteRangeStatus.Unsatisfiable, null, $"bytes */{totalLength}");
}

public static class HttpByteRangeParser
{
    public static HttpByteRangeResult Parse(string? rangeHeader, long totalLength)
    {
        if (totalLength < 0)
            throw new ArgumentOutOfRangeException(nameof(totalLength));

        if (string.IsNullOrWhiteSpace(rangeHeader))
            return HttpByteRangeResult.NoRange;

        const string prefix = "bytes=";
        if (!rangeHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return HttpByteRangeResult.NoRange;

        var spec = rangeHeader[prefix.Length..].Trim();
        if (spec.Length == 0 || spec.Contains(','))
            return HttpByteRangeResult.Unsatisfiable(totalLength);

        var separator = spec.IndexOf('-');
        if (separator < 0 || separator != spec.LastIndexOf('-'))
            return HttpByteRangeResult.Unsatisfiable(totalLength);

        var startToken = spec[..separator].Trim();
        var endToken = spec[(separator + 1)..].Trim();
        if (startToken.Length == 0)
            return ParseSuffixRange(endToken, totalLength);

        if (!TryParseNonNegative(startToken, out var start))
            return HttpByteRangeResult.Unsatisfiable(totalLength);

        if (totalLength == 0 || start >= totalLength)
            return HttpByteRangeResult.Unsatisfiable(totalLength);

        long end;
        if (endToken.Length == 0)
        {
            end = totalLength - 1;
        }
        else
        {
            if (!TryParseNonNegative(endToken, out end) || end < start)
                return HttpByteRangeResult.Unsatisfiable(totalLength);

            end = Math.Min(end, totalLength - 1);
        }

        return HttpByteRangeResult.Satisfiable(new HttpByteRange(start, end, totalLength));
    }

    private static HttpByteRangeResult ParseSuffixRange(string endToken, long totalLength)
    {
        if (!TryParseNonNegative(endToken, out var suffixLength) || suffixLength == 0 || totalLength == 0)
            return HttpByteRangeResult.Unsatisfiable(totalLength);

        var start = Math.Max(0, totalLength - suffixLength);
        var end = totalLength - 1;
        return HttpByteRangeResult.Satisfiable(new HttpByteRange(start, end, totalLength));
    }

    private static bool TryParseNonNegative(string value, out long result)
    {
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
    }
}
