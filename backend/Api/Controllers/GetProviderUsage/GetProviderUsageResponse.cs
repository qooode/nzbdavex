namespace NzbWebDAV.Api.Controllers.GetProviderUsage;

public class GetProviderUsageResponse : BaseApiResponse
{
    public List<ProviderUsageItem> Providers { get; set; } = new();

    public class ProviderUsageItem
    {
        // Index into the user's UsenetProviderConfig.Providers list. Stable for
        // the lifetime of one settings page render — same key the UI uses for
        // edit/delete actions, so the frontend can join without an extra ID.
        public int Index { get; set; }
        public string Host { get; set; } = string.Empty;
        public long BytesUsed { get; set; }
        public long? ByteLimit { get; set; }
        public bool OverLimit { get; set; }
    }
}
