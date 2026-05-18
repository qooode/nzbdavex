namespace NzbWebDAV.Config;

public class IndexerConfig
{
    // Global HTTP(S) proxy URL applied to every indexer that doesn't set its own ProxyUrl.
    // Empty/null = no proxy. Accepts http://host:port or http://user:pass@host:port.
    public string? ProxyUrl { get; set; }

    public List<ConnectionDetails> Indexers { get; set; } = [];

    public class ConnectionDetails
    {
        public required string Name { get; set; }
        public required string Url { get; set; }
        public required string ApiKey { get; set; }
        public bool Enabled { get; set; } = true;
        public string? UserAgent { get; set; }
        public int MaxRequestsPerMinute { get; set; } = 0;
        public bool EnableStrictMatching { get; set; } = false;
        // Per-indexer HTTP(S) proxy URL. Overrides the global ProxyUrl. Empty/null = inherit global.
        public string? ProxyUrl { get; set; }
        public ResultFilter? Filter { get; set; }
    }

    public class ResultFilter
    {
        // Master toggle. When false, all rules below are ignored regardless of value.
        public bool Enabled { get; set; } = false;

        // Drop rules. Each defaults to "no effect".
        // Skip releases where password != 0 (RAR-passworded or contains inner archive).
        public bool SkipPassworded { get; set; } = false;

        // Minimum download count to keep a release. 0 = disabled (any count is fine).
        public int MinGrabs { get; set; } = 0;

        // Grace window for the MinGrabs rule. Releases newer than this many hours bypass
        // the MinGrabs check (so a fresh post isn't punished for having no grabs yet).
        // 0 disables the grace window entirely (MinGrabs applies to everything).
        public int GrabsGraceHours { get; set; } = 6;

        // Drop releases older than N days that still have zero recorded grabs.
        // 0 = disabled.
        public int MaxAgeDaysWithoutGrabs { get; set; } = 0;

        // Ranking. When true, this indexer's items will be sorted by grabs descending
        // before merging with other indexers' results. Items missing the grabs attribute
        // sort to the bottom of this indexer's slice (treated as unknown).
        public bool PreferDownloaded { get; set; } = false;
    }
}
