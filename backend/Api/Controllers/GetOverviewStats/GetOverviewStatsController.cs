using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

[ApiController]
[Route("api/get-overview-stats")]
public class GetOverviewStatsController(
    DavDatabaseClient davDb,
    ActiveReadRegistry registry
) : BaseApiController
{
    private const long OneMinute = 60_000;
    private const long OneHour = 60 * OneMinute;
    private const long OneDay = 24 * OneHour;

    // Log-scale latency buckets in milliseconds. Last bucket is a catch-all up to int.MaxValue.
    private static readonly int[] LatencyBucketEdges =
    {
        0, 10, 25, 50, 100, 200, 400, 800, 1500, 3000, 6000, 12000, 30000, int.MaxValue
    };

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetOverviewStatsRequest(HttpContext);
        var response = await BuildAsync(request).ConfigureAwait(false);
        return Ok(response);
    }

    private async Task<GetOverviewStatsResponse> BuildAsync(GetOverviewStatsRequest request)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var is7d = request.Window == GetOverviewStatsRequest.OverviewWindow.Last7Days;
        var windowMs = is7d ? 7 * OneDay : OneDay;
        var windowStart = nowMs - windowMs;
        var bucketSize = is7d ? OneHour : OneMinute;

        await using var metrics = new MetricsDbContext();

        // Pull the raw fetches once and reuse for several aggregations.
        var fetches = await metrics.SegmentFetches
            .Where(x => x.At >= windowStart)
            .Select(x => new { x.At, x.Provider, x.Status, x.DurationMs, x.Retries })
            .ToListAsync().ConfigureAwait(false);

        var sessions = await metrics.ReadSessions
            .Where(x => x.EndedAt >= windowStart)
            .Select(x => new { x.StartedAt, x.EndedAt, x.DurationMs, x.BytesServed })
            .ToListAsync().ConfigureAwait(false);

        var liveTiles = BuildLiveTiles(fetches, sessions, nowMs);
        var throughput = BuildThroughput(fetches.Select(f => (f.At, f.Status)), sessions.Select(s => (s.EndedAt, s.BytesServed)), bucketSize);
        var providers = BuildProviders(fetches, windowStart, bucketSize, is7d);
        var heatmap = BuildHeatmap(fetches.Select(f => f.At), nowMs);
        var latency = BuildLatency(fetches.Where(f => f.Status == SegmentFetch.FetchStatus.Ok).Select(f => f.DurationMs));
        var errors = BuildErrors(fetches.Select(f => f.Status));
        var catalogue = await BuildCatalogueAsync().ConfigureAwait(false);
        var sessionsBlock = BuildSessionsBlock(sessions.Select(s => (s.DurationMs, s.BytesServed)));
        var indexers = await BuildIndexersAsync().ConfigureAwait(false);

        return new GetOverviewStatsResponse
        {
            Window = is7d ? "7d" : "24h",
            Tiles = liveTiles,
            Throughput = throughput,
            TotalArticles = throughput.Sum(p => p.Articles),
            TotalErrors = throughput.Sum(p => p.Errors),
            Providers = providers,
            Catalogue = catalogue,
            Sessions = sessionsBlock,
            Heatmap = heatmap,
            Latency = latency,
            Errors = errors,
            Indexers = indexers,
        };
    }

    private GetOverviewStatsResponse.LiveTiles BuildLiveTiles<TFetch, TSession>(
        IEnumerable<TFetch> fetches,
        IEnumerable<TSession> sessions,
        long nowMs
    ) where TFetch : class where TSession : class
    {
        // We want last 60 s slice; reuse the already-pulled rows by inspecting properties dynamically.
        var sinceMs = nowMs - OneMinute;
        long articles = 0, errors = 0, served = 0;
        foreach (var f in fetches.Cast<dynamic>())
        {
            if ((long)f.At < sinceMs) continue;
            articles++;
            if (f.Status != SegmentFetch.FetchStatus.Ok) errors++;
        }
        foreach (var s in sessions.Cast<dynamic>())
        {
            if ((long)s.EndedAt < sinceMs) continue;
            served += (long)s.BytesServed;
        }
        return new GetOverviewStatsResponse.LiveTiles
        {
            ActiveReads = registry.Count,
            ArticlesPerMinute = articles,
            ErrorsPerMinute = errors,
            BytesServedPerMinute = served,
        };
    }

    private static List<GetOverviewStatsResponse.ThroughputPoint> BuildThroughput(
        IEnumerable<(long At, SegmentFetch.FetchStatus Status)> fetches,
        IEnumerable<(long EndedAt, long BytesServed)> sessions,
        long bucketSize)
    {
        var byBucket = new Dictionary<long, (long Articles, long Errors, long BytesServed)>();
        foreach (var (at, status) in fetches)
        {
            var b = at - (at % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (cur.Articles + 1, cur.Errors + (status != SegmentFetch.FetchStatus.Ok ? 1 : 0), cur.BytesServed);
        }
        foreach (var (endedAt, bytes) in sessions)
        {
            var b = endedAt - (endedAt % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (cur.Articles, cur.Errors, cur.BytesServed + bytes);
        }

        return byBucket
            .OrderBy(kv => kv.Key)
            .Select(kv => new GetOverviewStatsResponse.ThroughputPoint
            {
                Bucket = kv.Key,
                Articles = kv.Value.Articles,
                Errors = kv.Value.Errors,
                BytesServed = kv.Value.BytesServed,
            })
            .ToList();
    }

    private static List<GetOverviewStatsResponse.ProviderRow> BuildProviders<T>(
        List<T> fetches, long windowStart, long bucketSize, bool is7d) where T : class
    {
        // Group rows by provider for the table aggregates plus a per-provider sparkline.
        var sparkBuckets = is7d ? 168 : 24;
        var sparkSize = is7d ? OneHour : OneHour;   // 24h view sparkline shows 24 hourly buckets
        var sparkStart = windowStart - (windowStart % sparkSize);

        var byProvider = new Dictionary<string, ProviderAccumulator>();
        foreach (var f in fetches.Cast<dynamic>())
        {
            string host = f.Provider;
            if (!byProvider.TryGetValue(host, out var acc))
                acc = new ProviderAccumulator(sparkBuckets);
            acc.Articles++;
            acc.SumDurationMs += f.DurationMs;
            if (f.Status != SegmentFetch.FetchStatus.Ok) acc.Errors++;
            acc.Retries += f.Retries;
            var idx = (int)(((long)f.At - sparkStart) / sparkSize);
            if (idx >= 0 && idx < sparkBuckets) acc.Spark[idx]++;
            byProvider[host] = acc;
        }

        return byProvider
            .Select(kv => new GetOverviewStatsResponse.ProviderRow
            {
                Provider = kv.Key,
                Articles = kv.Value.Articles,
                BytesFetched = 0,
                Errors = kv.Value.Errors,
                Retries = kv.Value.Retries,
                AvgDurationMs = kv.Value.Articles > 0 ? (double)kv.Value.SumDurationMs / kv.Value.Articles : 0,
                ErrorRate = kv.Value.Articles > 0 ? (double)kv.Value.Errors / kv.Value.Articles : 0,
                Spark = kv.Value.Spark.ToList(),
            })
            .OrderByDescending(r => r.Articles)
            .ToList();
    }

    private sealed class ProviderAccumulator
    {
        public long Articles, Errors, Retries, SumDurationMs;
        public readonly long[] Spark;
        public ProviderAccumulator(int n) { Spark = new long[n]; }
    }

    private static GetOverviewStatsResponse.HeatmapBlock BuildHeatmap(IEnumerable<long> fetchTimes, long nowMs)
    {
        // Last 7 days, bucketed by (UTC day-of-week, hour). Day is 0=Mon..6=Sun.
        var since = nowMs - 7 * OneDay;
        var cells = new Dictionary<(int Day, int Hour), long>();
        long max = 0;
        foreach (var at in fetchTimes)
        {
            if (at < since) continue;
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime;
            var dow = ((int)dt.DayOfWeek + 6) % 7; // shift Sun=0 → Mon=0
            var key = (dow, dt.Hour);
            cells.TryGetValue(key, out var c);
            c++;
            cells[key] = c;
            if (c > max) max = c;
        }

        return new GetOverviewStatsResponse.HeatmapBlock
        {
            MaxCell = max,
            Cells = cells
                .Select(kv => new GetOverviewStatsResponse.HeatmapCell
                {
                    Day = kv.Key.Day,
                    Hour = kv.Key.Hour,
                    Count = kv.Value,
                })
                .ToList(),
        };
    }

    private static GetOverviewStatsResponse.LatencyBlock BuildLatency(IEnumerable<int> okDurationsMs)
    {
        var samples = okDurationsMs.ToList();
        if (samples.Count == 0) return new GetOverviewStatsResponse.LatencyBlock();

        samples.Sort();
        int Pct(double p)
        {
            var idx = (int)Math.Ceiling(p * samples.Count) - 1;
            return samples[Math.Clamp(idx, 0, samples.Count - 1)];
        }

        var buckets = new List<GetOverviewStatsResponse.LatencyBucket>();
        for (var i = 0; i < LatencyBucketEdges.Length - 1; i++)
        {
            var lo = LatencyBucketEdges[i];
            var hi = LatencyBucketEdges[i + 1];
            var count = samples.Count(d => d >= lo && d < hi);
            if (count == 0 && lo > 0) continue; // hide empty buckets except the first one for axis context
            buckets.Add(new GetOverviewStatsResponse.LatencyBucket { LoMs = lo, HiMs = hi, Count = count });
        }

        return new GetOverviewStatsResponse.LatencyBlock
        {
            P50Ms = Pct(0.50),
            P95Ms = Pct(0.95),
            P99Ms = Pct(0.99),
            Samples = samples.Count,
            Buckets = buckets,
        };
    }

    private static List<GetOverviewStatsResponse.ErrorSlice> BuildErrors(IEnumerable<SegmentFetch.FetchStatus> statuses)
    {
        var counts = new Dictionary<SegmentFetch.FetchStatus, long>();
        foreach (var s in statuses)
        {
            if (s == SegmentFetch.FetchStatus.Ok) continue;
            counts.TryGetValue(s, out var c);
            counts[s] = c + 1;
        }

        return counts
            .Select(kv => new GetOverviewStatsResponse.ErrorSlice
            {
                Status = kv.Key.ToString(),
                Count = kv.Value,
            })
            .OrderByDescending(s => s.Count)
            .ToList();
    }

    private async Task<GetOverviewStatsResponse.CatalogueBlock> BuildCatalogueAsync()
    {
        var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

        var files = davDb.Ctx.Items.Where(i => i.Type == DavItem.ItemType.UsenetFile);
        var fileCount = await files.CountAsync().ConfigureAwait(false);
        var totalBytes = await files.SumAsync(i => (long?)i.FileSize).ConfigureAwait(false) ?? 0L;
        var largest = await files.MaxAsync(i => (long?)i.FileSize).ConfigureAwait(false) ?? 0L;
        var addedRecently = await files
            .Where(i => i.CreatedAt >= sevenDaysAgo)
            .CountAsync().ConfigureAwait(false);

        return new GetOverviewStatsResponse.CatalogueBlock
        {
            FileCount = fileCount,
            TotalBytes = totalBytes,
            LargestFileBytes = largest,
            AddedLast7Days = addedRecently,
        };
    }

    private static GetOverviewStatsResponse.SessionsBlock BuildSessionsBlock(
        IEnumerable<(int DurationMs, long BytesServed)> sessions)
    {
        var list = sessions.ToList();
        if (list.Count == 0) return new GetOverviewStatsResponse.SessionsBlock();

        return new GetOverviewStatsResponse.SessionsBlock
        {
            Count = list.Count,
            TotalBytesServed = list.Sum(x => x.BytesServed),
            AvgDurationMs = (long)list.Average(x => (double)x.DurationMs),
            LongestDurationMs = list.Max(x => x.DurationMs),
            BiggestReadBytes = list.Max(x => x.BytesServed),
        };
    }

    private async Task<List<GetOverviewStatsResponse.IndexerRow>> BuildIndexersAsync()
    {
        // Use HistoryItems for the last 30 days — that's where the real per-indexer performance data lives.
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var rows = await davDb.Ctx.HistoryItems
            .Where(h => h.CreatedAt >= cutoff && h.IndexerName != null)
            .GroupBy(h => h.IndexerName!)
            .Select(g => new
            {
                Name = g.Key,
                Completed = (long)g.Count(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed),
                Failed = (long)g.Count(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Failed),
                BytesCompleted = g
                    .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                    .Sum(x => (long?)x.TotalSegmentBytes) ?? 0L,
                AvgSecondsRaw = g
                    .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                    .Average(x => (double?)x.DownloadTimeSeconds),
            })
            .ToListAsync().ConfigureAwait(false);

        return rows
            .Select(r => new GetOverviewStatsResponse.IndexerRow
            {
                Name = r.Name,
                Completed = r.Completed,
                Failed = r.Failed,
                BytesCompleted = r.BytesCompleted,
                AvgSeconds = (int)(r.AvgSecondsRaw ?? 0),
                SuccessRate = r.Completed + r.Failed > 0 ? (double)r.Completed / (r.Completed + r.Failed) : 0,
            })
            .OrderByDescending(r => r.Completed + r.Failed)
            .ToList();
    }
}
