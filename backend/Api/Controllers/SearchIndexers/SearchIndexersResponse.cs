namespace NzbWebDAV.Api.Controllers.SearchIndexers;

public class SearchIndexersResponse : BaseApiResponse
{
    public List<Result> Results { get; set; } = [];
    public List<IndexerStatus> Indexers { get; set; } = [];

    public class Result
    {
        public required string Indexer { get; init; }
        public required string Title { get; init; }
        public required string NzbUrl { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Posted { get; init; }
    }

    public class IndexerStatus
    {
        public required string Name { get; init; }
        public required bool Ok { get; init; }
        public int ResultCount { get; init; }
        public string? Error { get; init; }
        public long ElapsedMs { get; init; }
    }
}
