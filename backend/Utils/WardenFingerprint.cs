using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Utils;

public static class WardenFingerprint
{
    public static string? Compute(long size, string? poster, DateTimeOffset? usenetDate)
    {
        if (size <= 0) return null;

        var hasPoster = !string.IsNullOrWhiteSpace(poster);
        var hasDate = usenetDate.HasValue;
        if (!hasPoster && !hasDate) return null;

        var posterNorm = hasPoster ? poster!.Trim().ToLowerInvariant() : "";
        var dayBucket = hasDate ? usenetDate!.Value.ToUnixTimeSeconds() / 86400 : 0;
        var canonical = $"{size}|{posterNorm}|{dayBucket}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "wd1:" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    public static string Backbone(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "unknown";
        var h = host.Trim().ToLowerInvariant();
        var colon = h.IndexOf(':');
        if (colon > 0) h = h[..colon];
        return string.IsNullOrEmpty(h) ? "unknown" : h;
    }
}
