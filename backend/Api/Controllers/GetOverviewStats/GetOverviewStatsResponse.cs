namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

public class GetOverviewStatsResponse
{
    public string Window { get; init; } = "24h";
    public LiveTiles Tiles { get; init; } = new();
    public List<ThroughputPoint> Throughput { get; init; } = new();
    public long TotalArticles { get; init; }
    public long TotalErrors { get; init; }
    public long TotalBytesFetched { get; init; }
    public List<ProviderRow> Providers { get; init; } = new();
    public CatalogueBlock Catalogue { get; init; } = new();
    public SessionsBlock Sessions { get; init; } = new();

    // Goated additions
    public HeatmapBlock Heatmap { get; init; } = new();
    public LatencyBlock Latency { get; init; } = new();
    public List<ErrorSlice> Errors { get; init; } = new();
    public List<IndexerRow> Indexers { get; init; } = new();
    public LifetimeBlock Lifetime { get; init; } = new();
    public RecordsBlock Records { get; init; } = new();

    public class LiveTiles
    {
        public int ActiveReads { get; init; }
        public long ArticlesPerMinute { get; init; }
        public long ErrorsPerMinute { get; init; }
        public long BytesServedPerMinute { get; init; }
    }

    public class ThroughputPoint
    {
        public long Bucket { get; init; }
        public long Articles { get; init; }
        public long Errors { get; init; }
        public long BytesServed { get; init; }
    }

    public class ProviderRow
    {
        public string Provider { get; init; } = "";
        public string? Nickname { get; init; }
        public long Articles { get; init; }
        public long BytesFetched { get; init; }
        public long Errors { get; init; }
        public long Retries { get; init; }
        public double AvgDurationMs { get; init; }
        public double ErrorRate { get; init; }
        public List<long> Spark { get; init; } = new();
    }

    public class CatalogueBlock
    {
        public long FileCount { get; init; }
        public long TotalBytes { get; init; }
        public long LargestFileBytes { get; init; }
        public long AddedLast7Days { get; init; }
    }

    public class SessionsBlock
    {
        public long Count { get; init; }
        public long TotalBytesServed { get; init; }
        public long AvgDurationMs { get; init; }
        public long LongestDurationMs { get; init; }
        public long BiggestReadBytes { get; init; }
    }

    /// <summary>
    /// Day-of-week × hour-of-day grid of article counts over the trailing 7 days.
    /// Day is 0 = Monday … 6 = Sunday (the user-friendly week start). Hour is 0–23 in UTC.
    /// Cells with zero activity are omitted.
    /// </summary>
    public class HeatmapBlock
    {
        public long MaxCell { get; init; }
        public List<HeatmapCell> Cells { get; init; } = new();
    }

    public class HeatmapCell
    {
        public int Day { get; init; }    // 0..6
        public int Hour { get; init; }   // 0..23
        public long Count { get; init; }
    }

    /// <summary>Fetch-duration percentiles + log-scale histogram for the window.</summary>
    public class LatencyBlock
    {
        public int P50Ms { get; init; }
        public int P95Ms { get; init; }
        public int P99Ms { get; init; }
        public int Samples { get; init; }
        public List<LatencyBucket> Buckets { get; init; } = new();
    }

    public class LatencyBucket
    {
        public int LoMs { get; init; }
        public int HiMs { get; init; }
        public long Count { get; init; }
    }

    /// <summary>Share of each fetch error type, for the donut.</summary>
    public class ErrorSlice
    {
        public string Status { get; init; } = "";
        public long Count { get; init; }
    }

    /// <summary>Per-indexer aggregate over the last 30 days from HistoryItems.</summary>
    public class IndexerRow
    {
        public string Name { get; init; } = "";
        public long Completed { get; init; }
        public long Failed { get; init; }
        public long BytesCompleted { get; init; }
        public int AvgSeconds { get; init; }
        public double SuccessRate { get; init; }
    }

    /// <summary>
    /// All-time totals across every minute the metrics database has retained. Values
    /// only grow; the dashboard renders them as the big "your forever stats" tiles.
    /// </summary>
    public class LifetimeBlock
    {
        public long BytesFetched { get; init; }
        public long BytesRead { get; init; }
        public long Articles { get; init; }
        public long ReadSessions { get; init; }
        public long ReadSeconds { get; init; }
        public long? FirstSeenAt { get; init; }
    }

    /// <summary>
    /// Personal-best records — "your busiest day", "your busiest hour". Bytes-fetched
    /// here is what the providers actually delivered (downstream of the byte tracker).
    /// </summary>
    public class RecordsBlock
    {
        public long BestDayBytes { get; init; }
        public long? BestDayAt { get; init; }
        public long BestHourBytes { get; init; }
        public long? BestHourAt { get; init; }
    }
}
