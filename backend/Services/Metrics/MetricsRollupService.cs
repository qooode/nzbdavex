using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Materializes per-minute and per-hour rollups from the raw SegmentFetch /
/// ReadSession event tables. Runs once a minute, idempotently upserting the
/// last fully-elapsed minute. On the hour boundary it folds the 60 finished
/// minutes into ProviderHourly. Re-running any window is safe.
/// </summary>
public class MetricsRollupService : BackgroundService
{
    private const long OneMinute = 60_000;
    private const long OneHour = 60 * OneMinute;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private long _lastMinuteRolled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await RollupTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MetricsRollupService tick failed");
            }
        }
    }

    private async Task RollupTickAsync()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentMinute = FloorTo(nowMs, OneMinute);
        var targetMinute = currentMinute - OneMinute;

        // Catch up at most 60 minutes if the service was paused/restarted.
        var start = _lastMinuteRolled == 0
            ? targetMinute
            : Math.Max(_lastMinuteRolled + OneMinute, targetMinute - 59 * OneMinute);

        await using var db = new MetricsDbContext();
        for (var minute = start; minute <= targetMinute; minute += OneMinute)
        {
            await RollupMinuteAsync(db, minute).ConfigureAwait(false);
            if (minute % OneHour == 0 && minute > 0)
                await RollupHourAsync(db, minute - OneHour).ConfigureAwait(false);
            _lastMinuteRolled = minute;
        }
    }

    private static async Task RollupMinuteAsync(MetricsDbContext db, long minute)
    {
        var next = minute + OneMinute;

        // ThroughputMinute: read-session bytes (downstream) + fetch bytes (upstream).
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ThroughputMinutes (Minute, BytesServed, BytesFetched, Articles, Errors, ActiveReadsMax)
            SELECT
                {0} AS Minute,
                COALESCE((SELECT SUM(BytesServed) FROM ReadSessions WHERE EndedAt >= {0} AND EndedAt < {1}), 0) AS BytesServed,
                COALESCE((SELECT SUM(Bytes)       FROM SegmentFetches WHERE At >= {0} AND At < {1}), 0)        AS BytesFetched,
                COALESCE((SELECT COUNT(*)         FROM SegmentFetches WHERE At >= {0} AND At < {1}), 0)        AS Articles,
                COALESCE((SELECT COUNT(*)         FROM SegmentFetches WHERE At >= {0} AND At < {1} AND Status <> 0), 0) AS Errors,
                0 AS ActiveReadsMax
            ON CONFLICT(Minute) DO UPDATE SET
                BytesServed  = excluded.BytesServed,
                BytesFetched = excluded.BytesFetched,
                Articles     = excluded.Articles,
                Errors       = excluded.Errors;
            """,
            minute, next).ConfigureAwait(false);

        // ProviderMinute: per-provider counters.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ProviderMinutes (Minute, Provider, Articles, BytesFetched, Errors, Retries, SumDurationMs, Hist)
            SELECT {0}, Provider,
                COUNT(*),
                SUM(Bytes),
                SUM(CASE WHEN Status <> 0 THEN 1 ELSE 0 END),
                SUM(Retries),
                SUM(DurationMs),
                NULL
            FROM SegmentFetches
            WHERE At >= {0} AND At < {1}
            GROUP BY Provider
            ON CONFLICT(Minute, Provider) DO UPDATE SET
                Articles      = excluded.Articles,
                BytesFetched  = excluded.BytesFetched,
                Errors        = excluded.Errors,
                Retries       = excluded.Retries,
                SumDurationMs = excluded.SumDurationMs;
            """,
            minute, next).ConfigureAwait(false);
    }

    private static async Task RollupHourAsync(MetricsDbContext db, long hour)
    {
        var next = hour + OneHour;
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ProviderHourly (Hour, Provider, Articles, BytesFetched, Errors, Retries, SumDurationMs, P95DurationMs)
            SELECT {0}, Provider,
                SUM(Articles),
                SUM(BytesFetched),
                SUM(Errors),
                SUM(Retries),
                SUM(SumDurationMs),
                NULL
            FROM ProviderMinutes
            WHERE Minute >= {0} AND Minute < {1}
            GROUP BY Provider
            ON CONFLICT(Hour, Provider) DO UPDATE SET
                Articles      = excluded.Articles,
                BytesFetched  = excluded.BytesFetched,
                Errors        = excluded.Errors,
                Retries       = excluded.Retries,
                SumDurationMs = excluded.SumDurationMs;
            """,
            hour, next).ConfigureAwait(false);
    }

    private static long FloorTo(long value, long step) => value - (value % step);
}
