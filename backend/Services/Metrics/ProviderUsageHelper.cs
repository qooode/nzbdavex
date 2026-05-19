using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Glue between the persistent metrics rollups and the in-memory byte tracker
/// for the per-provider data cap. Two responsibilities:
///   - hydrate <see cref="ProviderBytesTracker"/> from ProviderHourly so the
///     hot-path limit check has an accurate "bytes since reset" number after
///     restart or a config change,
///   - expose the same computation directly for read-only API consumers that
///     don't want to round-trip through the tracker.
///
/// The reset semantics are intentional: ResetAt is a unix-ms cutoff applied at
/// query time, not a DESTRUCTIVE delete of older rows. Historical graphs stay
/// intact across a reset; only the "bytes consumed against this block" gauge
/// rewinds. Offset is added on top so a user migrating from another client can
/// pre-seed "I've already burned 300 GB on this account" without faking events
/// into the metrics tables.
/// </summary>
public static class ProviderUsageHelper
{
    /// <summary>
    /// Fraction of the configured cap at which the provider is taken out of
    /// rotation. The headroom absorbs in-flight fetches that already passed
    /// the per-call check at <see cref="MultiProviderNntpClient"/> startup
    /// but haven't finished streaming bytes through CountingYencStream yet.
    /// 0.95 means "stop at 95% so the remaining 5% covers parallel fetches"
    /// — well above the worst realistic overshoot (MaxConnections × ~1 MB),
    /// and tiny compared to a typical multi-hundred-GB block.
    /// </summary>
    public const double EffectiveLimitFraction = 0.95;

    /// <summary>
    /// Computes raw bytes fetched for one provider since its last reset,
    /// summed from ProviderHourly. The caller adds <see cref="UsenetProviderConfig.ConnectionDetails.BytesUsedOffset"/>
    /// if it wants the user-facing total.
    /// </summary>
    public static async Task<long> ReadDbBytesSinceResetAsync(string host, long resetAt)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        await using var db = new MetricsDbContext();
        // SumAsync over nothing returns 0; no need to guard for empty.
        return await db.ProviderHourly
            .Where(x => x.Provider == host && x.Hour >= resetAt)
            .SumAsync(x => x.BytesFetched)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Walks every provider in <paramref name="config"/> and writes its
    /// since-reset byte total into <paramref name="tracker"/>. Best-effort —
    /// failures are logged but never thrown, since a metrics DB hiccup must
    /// not prevent the streaming client from starting up.
    /// </summary>
    public static async Task SeedTrackerAsync(ProviderBytesTracker tracker, UsenetProviderConfig config)
    {
        if (config.Providers.Count == 0) return;
        try
        {
            await using var db = new MetricsDbContext();
            // Distinct host so we don't issue duplicate queries for the unusual
            // case where two ConnectionDetails entries share a Host.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in config.Providers)
            {
                var host = provider.Host;
                if (string.IsNullOrEmpty(host) || !seen.Add(host)) continue;
                var bytes = await db.ProviderHourly
                    .Where(x => x.Provider == host && x.Hour >= provider.BytesUsedResetAt)
                    .SumAsync(x => x.BytesFetched)
                    .ConfigureAwait(false);
                tracker.SetLifetime(host, bytes);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to seed ProviderBytesTracker from metrics DB; continuing with zeros.");
        }
    }

    /// <summary>
    /// Total user-facing usage = bytes since reset (live in tracker) + offset.
    /// Clamped to 0 so a negative offset (manual correction) never reports as
    /// a negative gauge value.
    /// </summary>
    public static long ComputeUsage(ProviderBytesTracker tracker, UsenetProviderConfig.ConnectionDetails provider)
    {
        var live = tracker.GetLifetime(provider.Host);
        return Math.Max(0, live + provider.BytesUsedOffset);
    }

    /// <summary>
    /// True when a configured ByteLimit exists and the live counter has caught
    /// up to or passed the effective cutoff (configured limit × safety margin).
    /// A ByteLimit of null or 0 means "no cap".
    /// </summary>
    public static bool IsOverLimit(ProviderBytesTracker tracker, UsenetProviderConfig.ConnectionDetails provider)
    {
        var limit = provider.ByteLimit;
        if (!limit.HasValue || limit.Value <= 0) return false;
        var effective = (long)(limit.Value * EffectiveLimitFraction);
        return ComputeUsage(tracker, provider) >= effective;
    }
}
