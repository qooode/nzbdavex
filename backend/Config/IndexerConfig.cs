namespace NzbWebDAV.Config;

public class IndexerConfig
{
    public List<ConnectionDetails> Indexers { get; set; } = [];

    public class ConnectionDetails
    {
        public required string Name { get; set; }
        public required string Url { get; set; }
        public required string ApiKey { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
